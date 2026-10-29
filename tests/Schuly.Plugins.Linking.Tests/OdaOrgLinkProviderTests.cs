using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class OdaOrgLinkProviderTests
    {
        private const string Password = "Oda-Pa55word";
        private static readonly Guid User = Guid.NewGuid();

        private sealed class FakeScraper : OdaOrgScraper
        {
            public FakeScraper() : base(NullLogger<OdaOrgScraper>.Instance) { }
            public bool? Result = true;
            public string? LastBaseUrl;
            public override Task<bool?> CheckLoginAsync(string baseUrl, string username, string password, CancellationToken ct)
            {
                LastBaseUrl = baseUrl;
                return Task.FromResult(Result);
            }
        }

        private static (OdaOrgLinkProvider Provider, FakeScraper Scraper, OdaOrgDbContext Db, FakeVault Vault) Create()
        {
            var db = new OdaOrgDbContext(new DbContextOptionsBuilder<OdaOrgDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            var vault = new FakeVault();
            var scraper = new FakeScraper();
            var launcher = new OdaOrgSyncLauncher(new ServiceCollection().BuildServiceProvider(), new OdaOrgSyncTask(), NullLogger<OdaOrgSyncLauncher>.Instance);
            return (new OdaOrgLinkProvider(db, new OdaOrgSecretStore(vault), scraper, launcher), scraper, db, vault);
        }

        private static async Task<LinkStepResult> Start(OdaOrgLinkProvider p, string? reauth = null) =>
            await p.StartAsync(new LinkStartContext(User, "credentials", LinkPlatform.Web, reauth, "x"));

        [Fact]
        public async Task Start_ShowsTheCredentialsForm_WithTheDefaultPortal()
        {
            var (p, _, _, _) = Create();
            var form = await Start(p);
            Assert.Equal("credentials", form.Step.Id);
            var baseUrl = form.Step.Fields.Single(f => f.Key == "baseUrl");
            Assert.Equal(LinkFieldType.Url, baseUrl.Type);
            Assert.False(baseUrl.Required);
            Assert.Equal("https://odaorg.ict-bbag.ch", baseUrl.DefaultValue);
            Assert.True(form.Step.Fields.Single(f => f.Key == "password").Secret);
        }

        [Fact]
        public async Task WrongCredentials_StayOnTheStepWithError_AndSaveNothing()
        {
            var (p, scraper, db, vault) = Create();
            scraper.Result = false;
            var form = await Start(p);
            var result = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));
            Assert.Equal("credentials", result.Step.Id);
            Assert.NotNull(result.Step.Error);
            Assert.DoesNotContain(Password, StepAssert.Dump(result));
            Assert.Empty(db.Accounts);
            Assert.Equal(0, vault.Count);
        }

        [Fact]
        public async Task UnreachablePortal_StaysOnTheStepWithError()
        {
            var (p, scraper, db, _) = Create();
            scraper.Result = null;
            var form = await Start(p);
            var result = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));
            Assert.Equal("credentials", result.Step.Id);
            Assert.Empty(db.Accounts);
        }

        [Fact]
        public async Task VerifiedCredentials_SaveTheAccount_AndFinish()
        {
            var (p, scraper, db, vault) = Create();
            var form = await Start(p);
            var done = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            var account = Assert.Single(db.Accounts);
            Assert.Equal(account.Id.ToString(), done.Step.Done!.AccountId);
            Assert.Equal("https://odaorg.ict-bbag.ch", account.BaseUrl);
            Assert.Equal("https://odaorg.ict-bbag.ch", scraper.LastBaseUrl);
            Assert.Equal(1, vault.Count);
        }

        [Fact]
        public async Task LinkingTheSamePortalAgain_UpdatesTheAccount_NoDuplicate()
        {
            var (p, _, db, _) = Create();
            var form = await Start(p);
            var first = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));
            var second = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = "new" }));
            Assert.Equal(first.Step.Done!.AccountId, second.Step.Done!.AccountId);
            Assert.Single(db.Accounts);
        }

        [Fact]
        public async Task DisallowedPortal_IsRejectedBeforeAnyRequest()
        {
            var (p, scraper, _, _) = Create();
            var form = await Start(p);
            var result = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["baseUrl"] = "http://127.0.0.1", ["username"] = "u", ["password"] = Password }));
            Assert.Equal("credentials", result.Step.Id);
            Assert.Null(scraper.LastBaseUrl);
        }

        [Fact]
        public async Task Reauth_PrefillsThePortal_AndUnknownAccountsFail()
        {
            var (p, _, db, _) = Create();
            var account = new OdaOrgAccount { Id = Guid.NewGuid(), ApplicationUserId = User, BaseUrl = "https://portal.example.ch" };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();

            var form = await Start(p, account.Id.ToString());
            Assert.Equal("https://portal.example.ch", form.Step.Fields.Single(f => f.Key == "baseUrl").DefaultValue);
            Assert.Equal(LinkStepKind.Error, (await Start(p, Guid.NewGuid().ToString())).Step.Kind);
        }

        [Fact]
        public async Task ListAndUnlink()
        {
            var (p, _, db, vault) = Create();
            var form = await Start(p);
            var done = await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));

            var list = await p.ListAccountsAsync(User);
            var summary = Assert.Single(list);
            Assert.Equal("odaorg.ict-bbag.ch", summary.Title);
            Assert.True(summary.Status is LinkedAccountStatus.Syncing or LinkedAccountStatus.Ok or LinkedAccountStatus.Error);

            Assert.False(await p.UnlinkAsync(Guid.NewGuid(), done.Step.Done!.AccountId));
            Assert.True(await p.UnlinkAsync(User, done.Step.Done!.AccountId));
            Assert.Empty(db.Accounts);
            Assert.Equal(0, vault.Count);
        }

        [Fact]
        public async Task ExpiredCredentials_ShowAsExpired()
        {
            var (p, _, db, vault) = Create();
            var form = await Start(p);
            await p.SubmitAsync(StepAssert.Submit(User, "credentials", form, new() { ["username"] = "u", ["password"] = Password }));
            vault.Clear();
            var summary = Assert.Single(await p.ListAccountsAsync(User));
            Assert.Equal(LinkedAccountStatus.Expired, summary.Status);
        }

        [Fact]
        public void Descriptor_HasSchoolData()
        {
            var (p, _, _, _) = Create();
            Assert.Equal("odaorg", p.Descriptor.Id);
            Assert.Contains(p.Descriptor.Capabilities, c => c.Kind == "school_data");
        }
    }
}
