using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SchulnetzLinkProviderTests
    {
        private const string School = "https://schulnetz.example.ch";
        private const string Password = "Sch00l-Pa55!";
        private static readonly Guid User = Guid.NewGuid();

        private sealed class FakeConnector : ISchulwareConnector
        {
            public SchulwareConnectOutcome Outcome = new(true, Guid.NewGuid(), "Connected", ConnectFailure.None);
            public SchulwareConnectRequest? Last;
            public Task<SchulwareConnectOutcome> ConnectAsync(Guid userId, SchulwareConnectRequest request, CancellationToken cancellationToken = default)
            {
                Last = request;
                return Task.FromResult(Outcome);
            }
        }

        private sealed class FakeMethods : ISchulnetzLoginMethods
        {
            public SchulnetzLoginMethod Method = SchulnetzLoginMethod.Credentials;
            public Task<SchulnetzLoginMethod> DetectAsync(string baseUrl, CancellationToken cancellationToken = default) => Task.FromResult(Method);
            public Task<SchulnetzBrowserStart?> StartBrowserAsync(string baseUrl, CancellationToken cancellationToken = default) =>
                Task.FromResult<SchulnetzBrowserStart?>(new SchulnetzBrowserStart("https://school.example/auth", "https://callback.example/cb", BrowserSignIn.Serialize(new BrowserSignIn.Context("verifier", "device", "state-1"))));
        }

        private sealed class Env
        {
            public FakeConnector Connector = new();
            public FakeMethods Methods = new();
            public FakeLinkedAccounts Linked = new();
            public FakeVault Vault = new();
            public SchulwareDbContext Db = new(new DbContextOptionsBuilder<SchulwareDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            public SchulwareSyncLauncher Launcher = new(new ServiceCollection().BuildServiceProvider(), new SchulwareSyncTask(), NullLogger<SchulwareSyncLauncher>.Instance);
            public SchulnetzLinkProvider Provider => new(Db, Connector, Methods, Linked, new AccountSecretStore(Vault), Launcher, null!);
        }

        private static async Task<LinkStepResult> ToMethod(SchulnetzLinkProvider p, LinkPlatform platform = LinkPlatform.Web)
        {
            var school = await p.StartAsync(new LinkStartContext(User, "schulnetz", platform, null, "x"));
            return await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", school, new() { ["baseUrl"] = School }, platform: platform));
        }

        [Fact]
        public async Task Start_OffersTheSchoolSelect_WithCustomAllowed()
        {
            var p = new Env().Provider;
            var start = await p.StartAsync(new LinkStartContext(User, "schulnetz", LinkPlatform.Web, null, "x"));
            Assert.Equal("school", start.Step.Id);
            var field = Assert.Single(start.Step.Fields);
            Assert.Equal(LinkFieldType.Select, field.Type);
            Assert.True(field.AllowCustom);
            Assert.Equal(SchulnetzSchools.All.Count, field.Options.Count);
            Assert.Null(start.State);
        }

        [Fact]
        public async Task ABadSchoolAddress_StaysOnTheSchoolStepWithError()
        {
            var p = new Env().Provider;
            var school = await p.StartAsync(new LinkStartContext(User, "schulnetz", LinkPlatform.Web, null, "x"));
            var result = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", school, new() { ["baseUrl"] = "http://10.0.0.1" }));
            Assert.Equal("school", result.Step.Id);
            Assert.NotNull(result.Step.Error);
        }

        [Fact]
        public async Task CredentialsSchool_ShowsTheCredentialsForm_ThenDone()
        {
            var env = new Env();
            var p = env.Provider;
            var creds = await ToMethod(p);
            Assert.Equal("credentials", creds.Step.Id);
            Assert.Equal([LinkFieldType.Email, LinkFieldType.Password, LinkFieldType.TotpSecret], creds.Step.Fields.Select(f => f.Type));
            Assert.All(creds.Step.Fields.Where(f => f.Type != LinkFieldType.Email), f => Assert.True(f.Secret));
            Assert.False(creds.Step.Fields.Single(f => f.Key == "totp").Required);

            var done = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", creds, new() { ["email"] = "me@x.ch", ["password"] = Password }));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            Assert.Equal(env.Connector.Outcome.AccountId.ToString(), done.Step.Done!.AccountId);
            Assert.Equal("schulnetz.example.ch", done.Step.Done.Title);
            Assert.True(env.Connector.Last!.BackgroundSync);
        }

        [Fact]
        public async Task WrongCredentials_StayOnTheStepWithError_AndNeverLeakThePassword()
        {
            var env = new Env();
            env.Connector.Outcome = new(false, null, ApiError.LoginFailedMessage, ConnectFailure.Credentials);
            var p = env.Provider;
            var creds = await ToMethod(p);
            var wrong = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", creds, new() { ["email"] = "me@x.ch", ["password"] = Password, ["totp"] = "JBSWY3DPEHPK3PXP" }));
            Assert.Equal("credentials", wrong.Step.Id);
            Assert.Equal(ApiError.LoginFailedMessage, wrong.Step.Error);
            Assert.DoesNotContain(Password, StepAssert.Dump(wrong));
            Assert.DoesNotContain("JBSWY3DPEHPK3PXP", StepAssert.Dump(wrong));
            Assert.DoesNotContain(Password, StepAssert.Dump(creds));

            // The same state still works for the retry.
            env.Connector.Outcome = new(true, Guid.NewGuid(), "Connected", ConnectFailure.None);
            var done = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", wrong, new() { ["email"] = "me@x.ch", ["password"] = Password }));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
        }

        [Fact]
        public async Task MicrosoftSchool_ListsLinkedAccounts_AndOffersLinkingANewOne()
        {
            var env = new Env();
            var existing = await env.Linked.SaveAsync(User, new LinkedAccountRecord(LinkedProviders.Microsoft, "oid-1", "a@school.ch", "Alex"));
            env.Methods.Method = SchulnetzLoginMethod.Microsoft;
            var p = env.Provider;
            var select = await ToMethod(p);
            Assert.Equal("microsoft-account", select.Step.Id);
            Assert.Equal(LinkStepKind.Select, select.Step.Kind);
            Assert.Contains(select.Step.Options, o => o.Value == existing.Id.ToString());
            var link = Assert.Single(select.Step.Options, o => o.LinkProviderId == "microsoft");
            Assert.Equal("Link a new Microsoft account", link.Label);
            Assert.Contains(select.Step.Actions, a => a.Id == "refresh") ;
        }

        [Fact]
        public async Task RefreshAfterLinkingAnAccount_ShowsTheNewAccount()
        {
            var env = new Env();
            env.Methods.Method = SchulnetzLoginMethod.Microsoft;
            var p = env.Provider;
            var select = await ToMethod(p);
            Assert.DoesNotContain(select.Step.Options, o => o.LinkProviderId is null);

            var created = await env.Linked.SaveAsync(User, new LinkedAccountRecord(LinkedProviders.Microsoft, "oid-2", "new@school.ch", null));
            var refreshed = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", select, action: "refresh"));
            Assert.Equal("microsoft-account", refreshed.Step.Id);
            Assert.Contains(refreshed.Step.Options, o => o.Value == created.Id.ToString());
        }

        [Fact]
        public async Task PickingALinkedAccount_Connects_AndAnExpiredMicrosoftSignInStaysOnTheStep()
        {
            var env = new Env();
            var account = await env.Linked.SaveAsync(User, new LinkedAccountRecord(LinkedProviders.Microsoft, "oid-1", "a@school.ch", "Alex"));
            env.Methods.Method = SchulnetzLoginMethod.Microsoft;
            var p = env.Provider;
            var select = await ToMethod(p);

            env.Connector.Outcome = new(false, null, "The Microsoft sign-in of that account has expired. Link it again in Settings.", ConnectFailure.SignInExpired);
            var expired = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", select, new() { ["value"] = account.Id.ToString() }));
            Assert.Equal("microsoft-account", expired.Step.Id);
            Assert.NotNull(expired.Step.Error);

            env.Connector.Outcome = new(true, Guid.NewGuid(), "Connected", ConnectFailure.None);
            var done = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", expired, new() { ["value"] = account.Id.ToString() }));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            Assert.Equal(account.Id, env.Connector.Last!.LinkedAccountId);
        }

        [Fact]
        public async Task BrowserSchool_IsACaptureRedirectOnMobile_AndNeedsTheMobileAppOnWeb()
        {
            var env = new Env();
            env.Methods.Method = SchulnetzLoginMethod.Browser;
            var p = env.Provider;

            var web = await ToMethod(p, LinkPlatform.Web);
            Assert.Equal(LinkStepKind.Error, web.Step.Kind);
            Assert.Equal(SchulnetzLinkProvider.MobileOnlyMessage, web.Step.Error);

            var mobile = await ToMethod(p, LinkPlatform.Mobile);
            Assert.Equal(LinkStepKind.Redirect, mobile.Step.Kind);
            Assert.Equal(LinkRedirectMode.Capture, mobile.Step.Redirect!.Mode);
            Assert.Equal("https://callback.example/cb", mobile.Step.Redirect.CallbackPrefix);

            var done = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", mobile, new() { ["callbackUrl"] = "https://callback.example/cb?code=abc&state=state-1" }, platform: LinkPlatform.Mobile));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            Assert.Equal("abc", env.Connector.Last!.BrowserCode);
        }

        [Fact]
        public async Task BrowserCallbackWithTheWrongState_RestartsTheBrowserStepWithError()
        {
            var env = new Env();
            env.Methods.Method = SchulnetzLoginMethod.Browser;
            var p = env.Provider;
            var mobile = await ToMethod(p, LinkPlatform.Mobile);
            var again = await p.SubmitAsync(StepAssert.Submit(User, "schulnetz", mobile, new() { ["callbackUrl"] = "https://callback.example/cb?code=abc&state=other" }, platform: LinkPlatform.Mobile));
            Assert.Equal("browser", again.Step.Id);
            Assert.NotNull(again.Step.Error);
        }

        [Fact]
        public async Task Reauth_SkipsTheSchoolStep_AndUsesTheAccountsSchool()
        {
            var env = new Env();
            var account = new SchulwareAccount { Id = Guid.NewGuid(), ApplicationUserId = User, SchulnetzBaseUrl = School, SchulwareApiBaseUrl = "https://api" };
            env.Db.Accounts.Add(account);
            await env.Db.SaveChangesAsync();
            var p = env.Provider;

            var start = await p.StartAsync(new LinkStartContext(User, "schulnetz", LinkPlatform.Web, account.Id.ToString(), "x"));
            Assert.Equal("credentials", start.Step.Id);

            var other = await p.StartAsync(new LinkStartContext(Guid.NewGuid(), "schulnetz", LinkPlatform.Web, account.Id.ToString(), "x"));
            Assert.Equal(LinkStepKind.Error, other.Step.Kind);
        }

        [Fact]
        public async Task Accounts_ReportStatusFromTokensAndSyncState()
        {
            var env = new Env();
            SchulwareAccount Add(string host, DateTime created)
            {
                var a = new SchulwareAccount { Id = Guid.NewGuid(), ApplicationUserId = User, SchulnetzBaseUrl = $"https://{host}", SchulwareApiBaseUrl = "https://api", CreatedAt = created };
                env.Db.Accounts.Add(a);
                return a;
            }

            var fresh = Add("fresh.example.ch", DateTime.UtcNow);
            var ok = Add("ok.example.ch", DateTime.UtcNow.AddDays(-1));
            var expired = Add("expired.example.ch", DateTime.UtcNow.AddDays(-1));
            var failed = Add("failed.example.ch", DateTime.UtcNow.AddDays(-1));
            ok.DisplayName = "My school";
            var lastSync = DateTime.UtcNow.AddMinutes(-3);
            env.Db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), AccountId = ok.Id, LastSyncAt = lastSync, LastSyncStatus = "Success" });
            env.Db.SyncStates.Add(new SyncState { Id = Guid.NewGuid(), AccountId = failed.Id, LastSyncAt = lastSync, LastSyncStatus = "Failed", LastSyncError = "Grades failed" });
            await env.Db.SaveChangesAsync();
            var secrets = new AccountSecretStore(env.Vault);
            foreach (var a in new[] { fresh, ok, failed }) { a.MobileAccessToken = "t"; secrets.Save(a); }

            var list = await env.Provider.ListAccountsAsync(User);
            LinkedAccountSummary Of(SchulwareAccount a) => list.Single(s => s.Id == a.Id.ToString());

            Assert.Equal(LinkedAccountStatus.Syncing, Of(fresh).Status);
            Assert.Equal(LinkedAccountStatus.Ok, Of(ok).Status);
            Assert.Equal("My school", Of(ok).Title);
            Assert.Equal("ok.example.ch", Of(ok).Detail);
            Assert.NotNull(Of(ok).LastSyncedAt);
            Assert.Equal(LinkedAccountStatus.Expired, Of(expired).Status);
            Assert.Equal(LinkedAccountStatus.Error, Of(failed).Status);
            Assert.Equal("Grades failed", Of(failed).Message);
            Assert.Equal("failed.example.ch", Of(failed).Title);
        }

        [Fact]
        public void Descriptor_HasTheAbsenceAndSchoolDataCapabilities()
        {
            var d = new Env().Provider.Descriptor;
            Assert.Equal("schulnetz", d.Id);
            Assert.Contains(d.Capabilities, c => c.Kind == "absence_reporting" && c.BasePath == "/api/plugins/schulware/accounts");
            Assert.Contains(d.Capabilities, c => c.Kind == "school_data");
            Assert.Equal(LinkPlatforms.All, Assert.Single(d.Flows).Platforms);
        }

        [Fact]
        public void AbsenceErrorCodes_AreTheGenericOnes()
        {
            Assert.Equal("signin_expired", AbsenceReportException.SignInExpired().Code);
            Assert.Equal("school_unavailable", AbsenceReportException.Unavailable().Code);
            Assert.Equal("school_rejected", AbsenceReportException.Rejected("no").Code);
        }
    }
}
