using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Purge.Tests
{
    [NotInParallel("SchulwareClientFactory")]
    public class SchulwarePurgeTests
    {
        private sealed class ProfileHandler(string exitDate) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"firstName":"Test","lastName":"Student","exitDate":"{{exitDate}}"}""", Encoding.UTF8, "application/json") });
        }

        private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
        }

        private static (ServiceProvider Provider, FakeVault Vault, FakeSchoolData SchoolData) Start(string exitDate, bool retentionEnabled = true)
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["SchulwareApi:BaseUrl"] = "http://schulware.test", ["Retention:Enabled"] = retentionEnabled.ToString(), ["Retention:MonthsAfterLeave"] = "6" })
                .Build();
            services.AddSingleton<IConfiguration>(configuration);
            new SchulwarePlugin().ConfigureServices(services, new PluginServiceContext("Host=unused", configuration));
            var vault = new FakeVault();
            var schoolData = new FakeSchoolData();
            services.AddHostServices<SchulwareDbContext>(vault, schoolData);
            services.RemoveAll<IHttpClientFactory>();
            services.AddSingleton<IHttpClientFactory>(new Factory(new ProfileHandler(exitDate)));
            return (services.BuildServiceProvider(), vault, schoolData);
        }

        private static async Task<Guid> LinkAsync(IServiceProvider provider, Guid owner)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            var account = new SchulwareAccount { ApplicationUserId = owner, SchulnetzBaseUrl = "https://school.test", SchulwareApiBaseUrl = "http://schulware.test", SchoolUserId = Guid.NewGuid(), MobileAccessToken = "school-token", MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1) };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.SyncStates.Add(new SyncState { AccountId = account.Id, LastSyncStatus = "Success" });
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<AccountSecretStore>().Save(account);
            return account.Id;
        }

        private static async Task PurgeAsync(IServiceProvider provider, Guid owner)
        {
            using var scope = provider.CreateScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IPluginEventHandler<PluginUserPurgeEvent>>())
                await handler.HandleAsync(new PluginUserPurgeEvent(owner));
        }

        private static string DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-days).ToString("yyyy-MM-dd");

        [Test]
        public async Task Purge_leaves_no_account_or_secret_behind()
        {
            var (provider, vault, _) = Start(DaysAgo(1));
            var owner = Guid.NewGuid();
            await LinkAsync(provider, owner);

            await PurgeAsync(provider, owner);

            using var scope = provider.CreateScope();
            await Assert.That(await scope.ServiceProvider.GetRequiredService<SchulwareDbContext>().Accounts.CountAsync()).IsEqualTo(0);
            await Assert.That(vault.Count).IsEqualTo(0);
        }

        [Test]
        public async Task A_retry_after_a_vault_failure_still_finds_the_secrets()
        {
            var (provider, vault, _) = Start(DaysAgo(1));
            var owner = Guid.NewGuid();
            await LinkAsync(provider, owner);
            vault.FailNextRemove = true;

            await Assert.ThrowsAsync<InvalidOperationException>(() => PurgeAsync(provider, owner));
            await PurgeAsync(provider, owner);

            using var scope = provider.CreateScope();
            await Assert.That(await scope.ServiceProvider.GetRequiredService<SchulwareDbContext>().Accounts.CountAsync()).IsEqualTo(0);
            await Assert.That(vault.Count).IsEqualTo(0);
        }

        [Test]
        public async Task A_sync_running_during_a_purge_does_not_bring_the_secrets_back()
        {
            var (provider, vault, schoolData) = Start(DaysAgo(1));
            var owner = Guid.NewGuid();
            var accountId = await LinkAsync(provider, owner);
            schoolData.DuringEnsureStudent = () => PurgeAsync(provider, owner);

            try { await provider.GetRequiredService<SchulwareSyncTask>().SyncAccountAsync(accountId, provider); }
            catch (DbUpdateConcurrencyException) { }

            await Assert.That(vault.Count).IsEqualTo(0);
        }

        [Test]
        public async Task A_sync_removes_an_account_whose_student_left_past_retention_and_provisions_nothing()
        {
            var (provider, vault, schoolData) = Start(DaysAgo(400));
            var accountId = await LinkAsync(provider, Guid.NewGuid());

            await provider.GetRequiredService<SchulwareSyncTask>().SyncAccountAsync(accountId, provider);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            await Assert.That(await db.Accounts.CountAsync()).IsEqualTo(0);
            await Assert.That(await db.SyncStates.CountAsync()).IsEqualTo(0);
            await Assert.That(vault.Count).IsEqualTo(0);
            await Assert.That(schoolData.StudentsEnsured).IsEqualTo(0);
        }

        [Test]
        public async Task A_sync_keeps_an_account_whose_student_left_recently()
        {
            var (provider, _, schoolData) = Start(DaysAgo(30));
            var accountId = await LinkAsync(provider, Guid.NewGuid());

            await provider.GetRequiredService<SchulwareSyncTask>().SyncAccountAsync(accountId, provider);

            using var scope = provider.CreateScope();
            await Assert.That(await scope.ServiceProvider.GetRequiredService<SchulwareDbContext>().Accounts.CountAsync()).IsEqualTo(1);
            await Assert.That(schoolData.StudentsEnsured).IsEqualTo(1);
        }

        [Test]
        public async Task A_sync_keeps_the_account_when_retention_is_turned_off()
        {
            var (provider, _, _) = Start(DaysAgo(400), retentionEnabled: false);
            var accountId = await LinkAsync(provider, Guid.NewGuid());

            await provider.GetRequiredService<SchulwareSyncTask>().SyncAccountAsync(accountId, provider);

            using var scope = provider.CreateScope();
            await Assert.That(await scope.ServiceProvider.GetRequiredService<SchulwareDbContext>().Accounts.CountAsync()).IsEqualTo(1);
        }
    }
}
