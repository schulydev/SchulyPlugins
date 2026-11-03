using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.OdaOrg;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.OdaOrg.Services;

namespace Schuly.Plugin.Purge.Tests
{
    public class OdaOrgPurgeTests
    {
        private sealed class CountingScraper() : OdaOrgScraper(NullLogger<OdaOrgScraper>.Instance)
        {
            public readonly List<string> LoggedInAs = [];
            public override Task<OdaScrape?> ScrapeAsync(string baseUrl, string username, string password, CancellationToken ct)
            {
                LoggedInAs.Add(username);
                return Task.FromResult<OdaScrape?>(null);
            }
        }

        private static (ServiceProvider Provider, FakeVault Vault, CountingScraper Scraper) Start()
        {
            var services = new ServiceCollection();
            var configuration = new ConfigurationBuilder().Build();
            services.AddSingleton<IConfiguration>(configuration);
            new OdaOrgPlugin().ConfigureServices(services, new PluginServiceContext("Host=unused", configuration));
            var vault = new FakeVault();
            services.AddHostServices<OdaOrgDbContext>(vault, new FakeSchoolData());
            var scraper = new CountingScraper();
            services.AddSingleton<OdaOrgScraper>(scraper);
            return (services.BuildServiceProvider(), vault, scraper);
        }

        private static async Task<Guid> LinkAsync(IServiceProvider provider, FakeVault vault, Guid owner)
        {
            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            var account = new OdaOrgAccount { ApplicationUserId = owner, BaseUrl = "https://odaorg.ict-bbag.ch", Username = $"user-{owner}", Password = "Oda-Pa55word" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            db.SyncStates.Add(new SyncState { AccountId = account.Id, LastSyncStatus = "Success" });
            await db.SaveChangesAsync();
            scope.ServiceProvider.GetRequiredService<OdaOrgSecretStore>().Save(account);
            vault.Set($"avatar:{account.Id}", "hash|blob-key");
            return account.Id;
        }

        // The host only reaches a plugin's data through the handlers the plugin registers, the way PluginHost dispatches.
        private static async Task PurgeAsync(IServiceProvider provider, Guid owner)
        {
            using var scope = provider.CreateScope();
            foreach (var handler in scope.ServiceProvider.GetServices<IPluginEventHandler<PluginUserPurgeEvent>>())
                await handler.HandleAsync(new PluginUserPurgeEvent(owner));
        }

        [Test]
        public async Task Purge_removes_the_users_accounts_sync_state_and_vault_secrets()
        {
            var (provider, vault, _) = Start();
            var owner = Guid.NewGuid();
            var other = Guid.NewGuid();
            await LinkAsync(provider, vault, owner);
            var kept = await LinkAsync(provider, vault, other);

            await PurgeAsync(provider, owner);

            using var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            await Assert.That(await db.Accounts.Select(a => a.Id).ToListAsync()).IsEquivalentTo(new[] { kept });
            await Assert.That(await db.SyncStates.Select(s => s.AccountId).ToListAsync()).IsEquivalentTo(new[] { kept });
            await Assert.That(vault.Keys).IsEquivalentTo(new[] { $"account:{kept}", $"avatar:{kept}" });
        }

        [Test]
        public async Task Sync_stops_logging_in_with_the_stored_password_after_a_purge()
        {
            var (provider, vault, scraper) = Start();
            var owner = Guid.NewGuid();
            await LinkAsync(provider, vault, owner);

            await PurgeAsync(provider, owner);
            await provider.GetRequiredService<OdaOrgSyncTask>().ExecuteAsync(provider, CancellationToken.None);

            await Assert.That(scraper.LoggedInAs).IsEmpty();
        }

        [Test]
        public async Task A_second_purge_changes_nothing()
        {
            var (provider, vault, _) = Start();
            var owner = Guid.NewGuid();
            await LinkAsync(provider, vault, owner);

            await PurgeAsync(provider, owner);
            await PurgeAsync(provider, owner);

            using var scope = provider.CreateScope();
            await Assert.That(await scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>().Accounts.CountAsync()).IsEqualTo(0);
            await Assert.That(vault.Count).IsEqualTo(0);
        }

        [Test]
        public async Task Unlinking_also_removes_the_avatar_entry()
        {
            var (provider, vault, _) = Start();
            var owner = Guid.NewGuid();
            var accountId = await LinkAsync(provider, vault, owner);

            using var scope = provider.CreateScope();
            await scope.ServiceProvider.GetServices<ILinkProvider>().Single().UnlinkAsync(owner, accountId.ToString());

            await Assert.That(vault.Count).IsEqualTo(0);
        }
    }
}
