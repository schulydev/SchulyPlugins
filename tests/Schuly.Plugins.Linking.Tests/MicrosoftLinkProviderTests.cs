using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class MicrosoftLinkProviderTests
    {
        private const string Password = "S3cret-Pa55word!";
        private static readonly Guid User = Guid.NewGuid();

        private sealed class FakeApi : IMicrosoftSignInApi
        {
            public bool PasswordSignInConfigured { get; set; } = true;
            public Queue<PasswordSignInState> Start = new();
            public Queue<PasswordSignInState> Method = new();
            public Queue<PasswordSignInState> Otp = new();
            public Queue<PasswordSignInState> Status = new();
            public int StartCalls;
            public List<string> Cancelled = new();
            public SignInResult? BrowserResult;
            public string? LastVerifier;

            public Task<BrowserStart> StartBrowserLoginAsync(CancellationToken ct) => Task.FromResult(new BrowserStart("https://login.microsoftonline.com/authorize?x=1", "https://redirect.example/cb", "verifier-1"));
            public Task<SignInResult> CompleteBrowserLoginAsync(string code, string codeVerifier, CancellationToken ct)
            {
                LastVerifier = codeVerifier;
                return Task.FromResult(BrowserResult ?? new SignInResult("complete", "refresh-1", "oid-1", "a@school.ch", "A", null));
            }
            public Task<PasswordSignInState> StartPasswordAsync(Guid userId, string username, string password, CancellationToken ct) { StartCalls++; return Task.FromResult(Start.Dequeue()); }
            public Task<PasswordSignInState> ChooseMethodAsync(Guid userId, string signInId, string method, CancellationToken ct) => Task.FromResult(Method.Dequeue());
            public Task<PasswordSignInState> SubmitOtpAsync(Guid userId, string signInId, string code, CancellationToken ct) => Task.FromResult(Otp.Dequeue());
            public Task<PasswordSignInState> GetPasswordStatusAsync(Guid userId, string signInId, CancellationToken ct) => Task.FromResult(Status.Dequeue());
            public Task CancelPasswordAsync(Guid userId, string signInId, CancellationToken ct) { Cancelled.Add(signInId); return Task.CompletedTask; }
        }

        private static PasswordSignInState S(string status, string? id = "sid", string[]? methods = null, string? number = null, int? expires = null, int? left = null, string? code = null, string? refresh = null, string? oid = null, string? email = null) =>
            new(status, id, methods ?? [], number, expires, left, refresh, oid, email, "Alex", code, null);

        private static PasswordSignInState Complete(string oid = "oid-1", string email = "a@school.ch") => S("complete", refresh: "refresh-x", oid: oid, email: email);

        private static (MicrosoftLinkProvider Provider, FakeApi Api, FakeLinkedAccounts Accounts, CapturingLogger<MicrosoftLinkProvider> Log, PasswordAttemptLimiter Limiter) Create()
        {
            var api = new FakeApi();
            var accounts = new FakeLinkedAccounts();
            var log = new CapturingLogger<MicrosoftLinkProvider>();
            var limiter = new PasswordAttemptLimiter(TimeProvider.System);
            return (new MicrosoftLinkProvider(api, new MicrosoftAccountLinker(accounts), accounts, limiter, log), api, accounts, log, limiter);
        }

        private static Task<LinkStepResult> Credentials(MicrosoftLinkProvider p, LinkStepResult form, string pw = Password) =>
            p.SubmitAsync(StepAssert.Submit(User, "password", form, new() { ["email"] = "a@school.ch", ["password"] = pw }));

        private static async Task<LinkStepResult> StartPassword(MicrosoftLinkProvider p) =>
            await p.StartAsync(new LinkStartContext(User, "password", LinkPlatform.Web, null, "https://host/callback"));

        [Fact]
        public async Task NoMfa_GoesStraightToDone()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(Complete());
            var result = await Credentials(p, await StartPassword(p));
            Assert.Equal(LinkStepKind.Done, result.Step.Kind);
            Assert.Single(accounts.Rows);
            Assert.Equal(accounts.Rows[0].Account.Id.ToString(), result.Step.Done!.AccountId);
        }

        [Fact]
        public async Task WrongPassword_StaysOnCredentialsWithError()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("error", null, code: "invalid_credentials"));
            var result = await Credentials(p, await StartPassword(p));
            Assert.Equal("credentials", result.Step.Id);
            Assert.NotNull(result.Step.Error);
        }

        [Theory]
        [InlineData("unsupported", MicrosoftLinkProvider.Unsupported)]
        [InlineData("blocked", MicrosoftLinkProvider.BlockedMessage)]
        [InlineData("failed", MicrosoftLinkProvider.FailedMessage)]
        [InlineData("anything-else", MicrosoftLinkProvider.FailedMessage)]
        public async Task TerminalCodes_FailWithFixedTexts(string code, string expected)
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(new PasswordSignInState("error", null, [], null, null, null, null, null, null, null, code, "SERVICE TEXT must not show"));
            var result = await Credentials(p, await StartPassword(p));
            Assert.Equal(LinkStepKind.Error, result.Step.Kind);
            Assert.Equal(expected, result.Step.Error);
        }

        [Fact]
        public async Task RateLimited_StaysOnCredentialsWithAFixedError()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("error", null, code: "rate_limited"));
            var result = await Credentials(p, await StartPassword(p));
            Assert.Equal("credentials", result.Step.Id);
            Assert.Equal(MicrosoftLinkProvider.RateLimitedMessage, result.Step.Error);
        }

        [Fact]
        public async Task Working_KeepsThePushStep_UntilThePollKnowsTheNumber()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("working"));
            var select = await Credentials(p, await StartPassword(p));
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));
            Assert.Equal(LinkStepKind.PushCode, push.Step.Kind);
            Assert.Null(push.Step.PushCode!.Number);

            api.Status.Enqueue(S("working"));
            var still = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, "push", push.State, null));
            Assert.Equal(LinkStepKind.PushCode, still.Step.Kind);

            api.Status.Enqueue(S("push_pending", number: "55", expires: 90));
            var withNumber = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, "push", still.State, null));
            Assert.Equal("55", withNumber.Step.PushCode!.Number);

            api.Status.Enqueue(Complete());
            var done = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, "push", withNumber.State, null));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task Denied_UsesTheMethodsTheServiceReturns()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("push_pending", number: "1", expires: 60));
            var select = await Credentials(p, await StartPassword(p));
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));
            api.Status.Enqueue(S("denied", methods: ["otp"]));
            var back = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, "push", push.State, null));
            Assert.Equal("mfa-method", back.Step.Id);
            Assert.Equal(["otp"], back.Step.Options.Select(o => o.Value));
        }

        [Fact]
        public async Task OtpWithNoAttemptsLeft_GoesBackToTheMethodStep_AndExhaustedFails()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("otp_required"));
            var select = await Credentials(p, await StartPassword(p));
            var otp = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "otp" }));

            api.Otp.Enqueue(S("otp_invalid", left: 0));
            var back = await p.SubmitAsync(StepAssert.Submit(User, "password", otp, new() { ["code"] = "111111" }));
            Assert.Equal("mfa-method", back.Step.Id);
            Assert.NotNull(back.Step.Error);

            api.Otp.Enqueue(S("error", code: "otp_exhausted"));
            var dead = await p.SubmitAsync(StepAssert.Submit(User, "password", otp, new() { ["code"] = "222222" }));
            Assert.Equal(LinkStepKind.Error, dead.Step.Kind);
            Assert.Equal(MicrosoftLinkProvider.ExhaustedMessage, dead.Step.Error);
        }

        [Fact]
        public async Task Cancel_DeletesTheSignIn()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push"]));
            var select = await Credentials(p, await StartPassword(p));
            var cancelled = await p.SubmitAsync(StepAssert.Submit(User, "password", select, action: "cancel"));
            Assert.Equal(LinkStepKind.Error, cancelled.Step.Kind);
            Assert.Contains("sid", api.Cancelled);
        }

        [Fact]
        public async Task OnlyUnknownMfaMethods_FailsAndCancelsTheSignIn()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["phone-call"]));
            var result = await Credentials(p, await StartPassword(p));
            Assert.Equal(LinkStepKind.Error, result.Step.Kind);
            Assert.Contains("sid", api.Cancelled);
        }

        [Fact]
        public async Task MfaMethodStep_ListsOnlyOfferedMethods_PushFirst()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["otp", "push"]));
            var both = await Credentials(p, await StartPassword(p));
            Assert.Equal("mfa-method", both.Step.Id);
            Assert.Equal(["push", "otp"], both.Step.Options.Select(o => o.Value));

            api.Start.Enqueue(S("mfa_required", methods: ["otp"]));
            var onlyOtp = await Credentials(p, await StartPassword(p));
            Assert.Equal(["otp"], onlyOtp.Step.Options.Select(o => o.Value));
        }

        [Fact]
        public async Task Push_Approve_CompletesViaPoll()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("push_pending", number: "42", expires: 60));
            var select = await Credentials(p, await StartPassword(p));
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));
            Assert.Equal(LinkStepKind.PushCode, push.Step.Kind);
            Assert.Equal("42", push.Step.PushCode!.Number);
            Assert.InRange(push.Step.PushCode.PollIntervalSeconds, 2, 3);
            Assert.Contains(push.Step.Actions, a => a.Id == "use-otp");

            api.Status.Enqueue(S("push_pending"));
            var pending = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, push.Step.Id, push.State, null));
            Assert.Equal(LinkStepKind.PushCode, pending.Step.Kind);

            api.Status.Enqueue(Complete());
            var done = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, push.Step.Id, push.State, null));
            Assert.Equal(LinkStepKind.Done, done.Step.Kind);
            Assert.Single(accounts.Rows);
        }

        [Theory]
        [InlineData("denied")]
        [InlineData("timed_out")]
        public async Task Push_DeniedOrTimedOut_BackToMethodWithError(string status)
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("push_pending", number: "42", expires: 60));
            var select = await Credentials(p, await StartPassword(p));
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));
            api.Status.Enqueue(S(status));
            var result = await p.PollAsync(new LinkPollContext(User, "password", LinkPlatform.Web, push.Step.Id, push.State, null));
            Assert.Equal("mfa-method", result.Step.Id);
            Assert.NotNull(result.Step.Error);
        }

        [Fact]
        public async Task Otp_WrongThenRight()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["otp", "push"]));
            api.Method.Enqueue(S("otp_required"));
            var select = await Credentials(p, await StartPassword(p));
            var otp = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "otp" }));
            Assert.Equal(LinkStepKind.Otp, otp.Step.Kind);
            Assert.Equal(6, otp.Step.Otp!.Length);
            Assert.Contains(otp.Step.Actions, a => a.Id == "use-push");

            api.Otp.Enqueue(S("otp_invalid", left: 2));
            var wrong = await p.SubmitAsync(StepAssert.Submit(User, "password", otp, new() { ["code"] = "000000" }));
            Assert.Equal("otp", wrong.Step.Id);
            Assert.Contains("2 attempts", wrong.Step.Error);

            api.Otp.Enqueue(Complete());
            var right = await p.SubmitAsync(StepAssert.Submit(User, "password", wrong, new() { ["code"] = "123456" }));
            Assert.Equal(LinkStepKind.Done, right.Step.Kind);
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task SwitchActions_MoveBetweenPushAndOtp()
        {
            var (p, api, _, _, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("push_pending", number: "7", expires: 60));
            var select = await Credentials(p, await StartPassword(p));
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));

            api.Method.Enqueue(S("otp_required"));
            var otp = await p.SubmitAsync(StepAssert.Submit(User, "password", push, action: "use-otp"));
            Assert.Equal(LinkStepKind.Otp, otp.Step.Kind);

            api.Method.Enqueue(S("push_pending", number: "8", expires: 60));
            var again = await p.SubmitAsync(StepAssert.Submit(User, "password", otp, action: "use-push"));
            Assert.Equal(LinkStepKind.PushCode, again.Step.Kind);
            Assert.Equal("8", again.Step.PushCode!.Number);
        }

        [Fact]
        public async Task RelinkingTheSameAccount_ReturnsTheSameId_NoDuplicate()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(Complete());
            api.Start.Enqueue(Complete());
            var first = await Credentials(p, await StartPassword(p));
            var second = await Credentials(p, await StartPassword(p));
            Assert.Equal(first.Step.Done!.AccountId, second.Step.Done!.AccountId);
            Assert.Single(accounts.Rows);
            Assert.Equal("refresh-x", accounts.Secrets[(accounts.Rows[0].Account.Id, LinkedSecrets.RefreshToken)]);
        }

        [Fact]
        public async Task Relink_MatchesARowStoredUnderTheEmail_AndAnEmailWithDifferentCase()
        {
            var (p, api, accounts, _, _) = Create();
            await accounts.SaveAsync(User, new LinkedAccountRecord(LinkedProviders.Microsoft, "a@school.ch", "a@school.ch", "A"));
            api.Start.Enqueue(Complete("oid-1", "A@School.ch"));
            var result = await Credentials(p, await StartPassword(p));
            Assert.Single(accounts.Rows);
            Assert.Equal(accounts.Rows[0].Account.Id.ToString(), result.Step.Done!.AccountId);
        }

        [Fact]
        public async Task SignInWithoutObjectId_UsesALowercasedEmailAsExternalId()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(S("complete", refresh: "r", oid: null, email: "Mixed@School.ch"));
            await Credentials(p, await StartPassword(p));
            Assert.Equal("mixed@school.ch", accounts.Rows[0].Account.ExternalId);
        }

        [Fact]
        public async Task Password_NeverAppearsInStateStepsOrLogs()
        {
            var (p, api, _, log, _) = Create();
            api.Start.Enqueue(S("mfa_required", methods: ["push", "otp"]));
            api.Method.Enqueue(S("push_pending", number: "42", expires: 60));
            var form = await StartPassword(p);
            var select = await Credentials(p, form);
            var push = await p.SubmitAsync(StepAssert.Submit(User, "password", select, new() { ["value"] = "push" }));
            Assert.DoesNotContain(Password, StepAssert.Dump(select));
            Assert.DoesNotContain(Password, StepAssert.Dump(push));
            Assert.DoesNotContain(Password, string.Join("\n", log.Lines));

            api.Start.Enqueue(S("error", null, code: "invalid_credentials"));
            var wrong = await Credentials(p, form);
            Assert.DoesNotContain(Password, StepAssert.Dump(wrong));
            api.Start.Enqueue(S("error", null, code: "unsupported"));
            Assert.DoesNotContain(Password, StepAssert.Dump(await Credentials(p, form)));
            Assert.DoesNotContain(Password, string.Join("\n", log.Lines));
        }

        private sealed class ThrowingApi : IMicrosoftSignInApi
        {
            public bool PasswordSignInConfigured => true;
            public Task<BrowserStart> StartBrowserLoginAsync(CancellationToken ct) => throw new HttpRequestException($"boom {Password}");
            public Task<SignInResult> CompleteBrowserLoginAsync(string code, string codeVerifier, CancellationToken ct) => throw new HttpRequestException($"boom {Password}");
            public Task<PasswordSignInState> StartPasswordAsync(Guid userId, string username, string password, CancellationToken ct) => throw new HttpRequestException($"failed for {password}");
            public Task<PasswordSignInState> ChooseMethodAsync(Guid userId, string signInId, string method, CancellationToken ct) => throw new HttpRequestException();
            public Task<PasswordSignInState> SubmitOtpAsync(Guid userId, string signInId, string code, CancellationToken ct) => throw new HttpRequestException();
            public Task<PasswordSignInState> GetPasswordStatusAsync(Guid userId, string signInId, CancellationToken ct) => throw new HttpRequestException();
            public Task CancelPasswordAsync(Guid userId, string signInId, CancellationToken ct) => Task.CompletedTask;
        }

        [Fact]
        public async Task ServiceFailure_DoesNotLeakThePasswordIntoLogsOrErrors()
        {
            var accounts = new FakeLinkedAccounts();
            var log = new CapturingLogger<MicrosoftLinkProvider>();
            var p = new MicrosoftLinkProvider(new ThrowingApi(), new MicrosoftAccountLinker(accounts), accounts, new PasswordAttemptLimiter(TimeProvider.System), log);
            var form = await StartPassword(p);
            var result = await Credentials(p, form);
            Assert.Equal(LinkStepKind.Error, result.Step.Kind);
            Assert.DoesNotContain(Password, StepAssert.Dump(result));
            Assert.DoesNotContain(Password, string.Join("\n", log.Lines));
            Assert.NotEmpty(log.Lines);
        }

        [Fact]
        public async Task PasswordFlow_IsNotOfferedOrCallable_WithoutTheServiceKey()
        {
            var (p, api, _, _, _) = Create();
            api.PasswordSignInConfigured = false;
            Assert.DoesNotContain(p.Descriptor.Flows, f => f.Id == "password");
            Assert.Contains(p.Descriptor.Flows, f => f.Id == "browser");
            var start = await p.StartAsync(new LinkStartContext(User, "password", LinkPlatform.Web, null, "x"));
            Assert.Equal(LinkStepKind.Error, start.Step.Kind);
            Assert.Equal(0, api.StartCalls);
        }

        [Fact]
        public void Descriptor_PasswordIsWebOnly_BrowserIsMobileOnly_AndHasCapabilities()
        {
            var (p, _, _, _, _) = Create();
            Assert.Equal(LinkPlatforms.Web, p.Descriptor.Flows.Single(f => f.Id == "password").Platforms);
            Assert.Equal(LinkPlatforms.Mobile, p.Descriptor.Flows.Single(f => f.Id == "browser").Platforms);
            Assert.Equal(["mail", "chat", "drive", "notes", "calendar"], p.Descriptor.Capabilities.Select(c => c.Kind));
            Assert.All(p.Descriptor.Capabilities, c => Assert.Equal("/api/plugins/microsoft/accounts", c.BasePath));
        }

        [Fact]
        public async Task FiveWrongPasswords_BlockFurtherAttemptsWithoutCallingTheService()
        {
            var (p, api, _, _, _) = Create();
            var form = await StartPassword(p);
            for (var i = 0; i < 5; i++)
            {
                api.Start.Enqueue(S("error", null, code: "invalid_credentials"));
                await Credentials(p, form);
            }
            var calls = api.StartCalls;
            var blocked = await Credentials(p, form);
            Assert.Equal(calls, api.StartCalls);
            Assert.Contains("Too many", blocked.Step.Error);
        }

        [Fact]
        public void Limiter_CountsOnlyFailures_PerUser_AndExpires()
        {
            var clock = new ManualTime();
            var limiter = new PasswordAttemptLimiter(clock);
            var other = Guid.NewGuid();
            for (var i = 0; i < 4; i++) limiter.RecordFailure(User);
            Assert.False(limiter.IsBlocked(User));
            limiter.RecordFailure(User);
            Assert.True(limiter.IsBlocked(User));
            Assert.False(limiter.IsBlocked(other));
            clock.Advance(TimeSpan.FromMinutes(16));
            Assert.False(limiter.IsBlocked(User));
            limiter.RecordFailure(User);
            limiter.Reset(User);
            Assert.False(limiter.IsBlocked(User));
        }

        private sealed class ManualTime : TimeProvider
        {
            private DateTimeOffset now = DateTimeOffset.UtcNow;
            public override DateTimeOffset GetUtcNow() => now;
            public void Advance(TimeSpan by) => now += by;
        }

        [Fact]
        public async Task BrowserFlow_IsACaptureRedirect_AndRelinkKeepsOneAccount()
        {
            var (p, api, accounts, _, _) = Create();
            var start = await p.StartAsync(new LinkStartContext(User, "browser", LinkPlatform.Mobile, null, "x"));
            Assert.Equal(LinkStepKind.Redirect, start.Step.Kind);
            Assert.Equal(LinkRedirectMode.Capture, start.Step.Redirect!.Mode);
            Assert.Equal("https://redirect.example/cb", start.Step.Redirect.CallbackPrefix);
            Assert.Contains("login.microsoftonline.com", start.Step.Redirect.CaptureCookieDomains);

            var cookies = "[{\"domain\":\"login.microsoftonline.com\",\"name\":\"ESTSAUTH\",\"path\":\"/\",\"value\":\"v\"}]";
            var values = new Dictionary<string, string> { ["callbackUrl"] = "https://redirect.example/cb?code=abc", ["cookies"] = cookies };
            var first = await p.SubmitAsync(StepAssert.Submit(User, "browser", start, values, platform: LinkPlatform.Mobile));
            Assert.Equal(LinkStepKind.Done, first.Step.Kind);
            Assert.Equal("verifier-1", api.LastVerifier);
            Assert.True(accounts.Secrets.ContainsKey((accounts.Rows[0].Account.Id, LinkedSecrets.SessionCookies)));

            var start2 = await p.StartAsync(new LinkStartContext(User, "browser", LinkPlatform.Mobile, null, "x"));
            var second = await p.SubmitAsync(StepAssert.Submit(User, "browser", start2, values, platform: LinkPlatform.Mobile));
            Assert.Equal(first.Step.Done!.AccountId, second.Step.Done!.AccountId);
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task BrowserFlow_IsRefusedOnWeb_AndPasswordFlowOnMobile()
        {
            var (p, _, _, _, _) = Create();
            Assert.Equal(LinkStepKind.Error, (await p.StartAsync(new LinkStartContext(User, "browser", LinkPlatform.Web, null, "x"))).Step.Kind);
            Assert.Equal(LinkStepKind.Error, (await p.StartAsync(new LinkStartContext(User, "password", LinkPlatform.Mobile, null, "x"))).Step.Kind);
        }

        [Fact]
        public async Task ListAndUnlink()
        {
            var (p, api, accounts, _, _) = Create();
            api.Start.Enqueue(Complete());
            var done = await Credentials(p, await StartPassword(p));
            var list = await p.ListAccountsAsync(User);
            Assert.Single(list);
            Assert.Equal(LinkedAccountStatus.Ok, list[0].Status);
            Assert.Equal("a@school.ch", list[0].Detail);
            Assert.False(await p.UnlinkAsync(Guid.NewGuid(), done.Step.Done!.AccountId));
            Assert.True(await p.UnlinkAsync(User, done.Step.Done!.AccountId));
            Assert.Empty(accounts.Rows);
        }
    }
}
