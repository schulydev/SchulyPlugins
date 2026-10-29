using System.Text.Json;
using System.Web;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>
    /// Links Microsoft 365 accounts. Flow "password" (web): email and password, then Microsoft's second factor when it asks for one.
    /// Flow "browser" (mobile): the WebView sign-in. The password is read from the submitted values, sent once and never kept, logged or put in the step state.
    /// </summary>
    public class MicrosoftLinkProvider(IMicrosoftSignInApi api, MicrosoftAccountLinker linker, ILinkedAccounts linkedAccounts, PasswordAttemptLimiter limiter, ILogger<MicrosoftLinkProvider> logger) : ILinkProvider
    {
        public const string ProviderId = "microsoft";
        public const string PasswordFlow = "password";
        public const string BrowserFlow = "browser";
        public const string CredentialsStep = "credentials";
        public const string MethodStep = "mfa-method";
        public const string PushStep = "push";
        public const string OtpStep = "otp";
        public const string BrowserStep = "browser";
        public const string UseOtpAction = "use-otp";
        public const string UsePushAction = "use-push";
        public const string Unsupported = "Your school's Microsoft sign-in isn't supported here yet. Use the Schuly mobile app to link this account.";
        public const string BlockedMessage = "Your school's Microsoft policy blocks this sign-in here. Use the Schuly mobile app to link this account.";
        public const string RateLimitedMessage = "Too many attempts, try again later.";
        public const string FailedMessage = "The Microsoft sign-in failed. Start again.";
        public const string ExhaustedMessage = "Too many wrong codes. Start again.";
        public const string ExpiredMessage = "The sign-in expired. Start again.";
        private const int PollIntervalSeconds = 2;

        private sealed record State(string? SignInId, List<string>? Methods, string? Number, DateTimeOffset? ExpiresAt, string? CodeVerifier);

        public LinkProviderDescriptor Descriptor => new()
        {
            Id = ProviderId,
            DisplayName = "Microsoft 365",
            Description = "Mail, Teams, OneDrive, OneNote and calendar.",
            Icon = new LinkIcon { Lucide = "layout-grid" },
            SortOrder = 2,
            Flows = Flows(),
            Capabilities =
            [
                new ProviderCapability("mail", "Mail", "mail", "/api/plugins/microsoft/accounts"),
                new ProviderCapability("chat", "Teams", "messages-square", "/api/plugins/microsoft/accounts"),
                new ProviderCapability("drive", "OneDrive", "hard-drive", "/api/plugins/microsoft/accounts"),
                new ProviderCapability("notes", "OneNote", "notebook", "/api/plugins/microsoft/accounts"),
                new ProviderCapability("calendar", "Calendar", "calendar", "/api/plugins/microsoft/accounts"),
            ],
        };

        private LinkFlowDescriptor[] Flows()
        {
            var flows = new List<LinkFlowDescriptor>();
            // Never offered without the service key: the password routes of SchulyMicrosoft must not be called without it.
            if (api.PasswordSignInConfigured)
                flows.Add(new LinkFlowDescriptor { Id = PasswordFlow, Label = "Email and password", Description = "Sign in with your Microsoft email and password.", Platforms = LinkPlatforms.Web });
            flows.Add(new LinkFlowDescriptor { Id = BrowserFlow, Label = "Microsoft sign-in", Description = "Sign in on Microsoft's own page.", Platforms = LinkPlatforms.Mobile });
            return [.. flows];
        }

        public async Task<LinkStepResult> StartAsync(LinkStartContext context, CancellationToken cancellationToken = default)
        {
            switch (context.FlowId)
            {
                case PasswordFlow when context.Platform == LinkPlatform.Web && api.PasswordSignInConfigured:
                    string? email = null;
                    if (Guid.TryParse(context.ReauthAccountId, out var reauthId))
                        email = (await linkedAccounts.GetAsync(context.ApplicationUserId, reauthId, cancellationToken))?.Email;
                    return new LinkStepResult(CredentialsForm(email, null), null);
                case BrowserFlow when context.Platform == LinkPlatform.Mobile:
                    return await BrowserRedirectAsync(null, cancellationToken);
                default:
                    return LinkStepResult.Fail("This sign-in method is not available here.");
            }
        }

        public async Task<LinkStepResult> SubmitAsync(LinkSubmitContext context, CancellationToken cancellationToken = default)
        {
            if (context.Action == LinkStepValidator.CancelAction)
            {
                await CancelAsync(context.ApplicationUserId, context.State);
                return LinkStepResult.Fail("The sign-in was cancelled.");
            }

            if (context.FlowId == BrowserFlow)
                return context.StepId == BrowserStep ? await CompleteBrowserAsync(context, cancellationToken) : LinkStepResult.Fail("Unexpected step.");
            if (context.FlowId != PasswordFlow || !api.PasswordSignInConfigured)
                return LinkStepResult.Fail("This sign-in method is not available here.");

            try
            {
                return context.StepId switch
                {
                    CredentialsStep => await SubmitCredentialsAsync(context, cancellationToken),
                    MethodStep => await SubmitMethodAsync(context, cancellationToken),
                    PushStep => await SubmitPushAsync(context, cancellationToken),
                    OtpStep => await SubmitOtpAsync(context, cancellationToken),
                    _ => LinkStepResult.Fail("Unexpected step."),
                };
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                // Only the exception type is logged: nothing the user submitted.
                logger.LogWarning("Microsoft password sign-in failed ({ExceptionType})", ex.GetType().Name);
                return LinkStepResult.Fail("Microsoft could not be reached. Try again later.");
            }
        }

        public async Task<LinkStepResult> PollAsync(LinkPollContext context, CancellationToken cancellationToken = default)
        {
            var state = Read(context.State);
            if (context.StepId != PushStep || state?.SignInId is null)
                return LinkStepResult.Fail(ExpiredMessage);
            try
            {
                var status = await api.GetPasswordStatusAsync(context.ApplicationUserId, state.SignInId, cancellationToken);
                switch (status.Status)
                {
                    case "push_pending":
                        var next = status.Number is null ? state : state with { Number = status.Number, ExpiresAt = status.ExpiresInSeconds is { } secs ? DateTimeOffset.UtcNow.AddSeconds(secs) : state.ExpiresAt };
                        return new LinkStepResult(PushForm(next, null), Write(next));
                    case "working":
                        return new LinkStepResult(PushForm(state, null), context.State);
                    case "complete":
                        return await CompleteAsync(context.ApplicationUserId, status, null, cancellationToken);
                    case "denied":
                        return MethodSelect(WithMethods(state, status.Methods), "The sign-in was denied on your device. Choose a method to try again.");
                    case "timed_out":
                        return MethodSelect(WithMethods(state, status.Methods), "The approval timed out. Choose a method to try again.");
                    default:
                        return Terminal(status) ?? MethodSelect(state, "Microsoft could not confirm the sign-in. Try again.");
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                logger.LogWarning("Microsoft push status failed ({ExceptionType})", ex.GetType().Name);
                return new LinkStepResult(PushForm(state, null), context.State);
            }
        }

        public async Task<IReadOnlyList<LinkedAccountSummary>> ListAccountsAsync(Guid applicationUserId, CancellationToken cancellationToken = default)
        {
            var accounts = await linkedAccounts.ListAsync(applicationUserId, LinkedProviders.Microsoft, cancellationToken);
            var result = new List<LinkedAccountSummary>();
            foreach (var account in accounts)
            {
                var hasToken = !string.IsNullOrEmpty(await linkedAccounts.GetSecretAsync(applicationUserId, account.Id, LinkedSecrets.RefreshToken, cancellationToken));
                result.Add(new LinkedAccountSummary(account.Id.ToString(), account.DisplayName ?? account.Email, account.Email, hasToken ? LinkedAccountStatus.Ok : LinkedAccountStatus.Expired, account.CreatedAt, hasToken ? null : "The sign-in expired. Link the account again.", null));
            }
            return result;
        }

        public Task<bool> UnlinkAsync(Guid applicationUserId, string accountId, CancellationToken cancellationToken = default) =>
            Guid.TryParse(accountId, out var id) ? linkedAccounts.RemoveAsync(applicationUserId, id, cancellationToken) : Task.FromResult(false);

        private async Task<LinkStepResult> SubmitCredentialsAsync(LinkSubmitContext context, CancellationToken ct)
        {
            var userId = context.ApplicationUserId;
            context.Values.TryGetValue("email", out var email);
            context.Values.TryGetValue("password", out var password);
            if (string.IsNullOrWhiteSpace(email) || string.IsNullOrEmpty(password))
                return new LinkStepResult(CredentialsForm(email, "Enter your email and password."), null);
            if (limiter.IsBlocked(userId))
                return new LinkStepResult(CredentialsForm(email, "Too many failed attempts. Wait a few minutes and try again."), null);

            var result = await api.StartPasswordAsync(userId, email.Trim(), password, ct);
            switch (result.Status)
            {
                case "complete":
                    limiter.Reset(userId);
                    return await CompleteAsync(userId, result, null, ct);
                case "mfa_required":
                    var methods = Supported(result.Methods);
                    if (result.SignInId is null || methods.Count == 0)
                    {
                        if (result.SignInId is not null) await CancelQuietlyAsync(userId, result.SignInId);
                        return LinkStepResult.Fail(Unsupported);
                    }
                    limiter.Reset(userId);
                    return MethodSelect(new State(result.SignInId, methods, null, null, null), null);
                default:
                    if (result.ErrorCode == "invalid_credentials")
                    {
                        limiter.RecordFailure(userId);
                        return new LinkStepResult(CredentialsForm(email, "The email or password is wrong."), null);
                    }
                    if (result.ErrorCode == "rate_limited")
                        return new LinkStepResult(CredentialsForm(email, RateLimitedMessage), null);
                    if (result.ErrorCode == "invalid")
                        return new LinkStepResult(CredentialsForm(email, "Check the email and password and try again."), null);
                    return Terminal(result) ?? LinkStepResult.Fail(FailedMessage);
            }
        }

        private async Task<LinkStepResult> SubmitMethodAsync(LinkSubmitContext context, CancellationToken ct)
        {
            var state = Read(context.State);
            if (state?.SignInId is null)
                return LinkStepResult.Fail(ExpiredMessage);
            context.Values.TryGetValue("value", out var method);
            return await ChooseAsync(context.ApplicationUserId, state, method, ct);
        }

        private async Task<LinkStepResult> SubmitPushAsync(LinkSubmitContext context, CancellationToken ct)
        {
            var state = Read(context.State);
            if (state?.SignInId is null)
                return LinkStepResult.Fail(ExpiredMessage);
            return context.Action == UseOtpAction ? await ChooseAsync(context.ApplicationUserId, state, "otp", ct) : new LinkStepResult(PushForm(state, null), context.State);
        }

        private async Task<LinkStepResult> SubmitOtpAsync(LinkSubmitContext context, CancellationToken ct)
        {
            var state = Read(context.State);
            if (state?.SignInId is null)
                return LinkStepResult.Fail(ExpiredMessage);
            if (context.Action == UsePushAction)
                return await ChooseAsync(context.ApplicationUserId, state, "push", ct);

            context.Values.TryGetValue("code", out var code);
            if (string.IsNullOrEmpty(code))
                return new LinkStepResult(OtpForm(state, null), context.State);

            var result = await api.SubmitOtpAsync(context.ApplicationUserId, state.SignInId, code, ct);
            switch (result.Status)
            {
                case "complete":
                    return await CompleteAsync(context.ApplicationUserId, result, null, ct);
                case "otp_invalid" when result.AttemptsLeft == 0:
                    return MethodSelect(WithMethods(state, result.Methods), "Too many wrong codes. Choose a method to try again.");
                case "otp_invalid":
                    var left = result.AttemptsLeft is { } n ? $" {n} attempt{(n == 1 ? "" : "s")} left." : "";
                    return new LinkStepResult(OtpForm(state, "That code is not right." + left), context.State);
                default:
                    if (result.ErrorCode == "rate_limited")
                        return new LinkStepResult(OtpForm(state, RateLimitedMessage), context.State);
                    return Terminal(result) ?? MethodSelect(state, "Microsoft could not confirm the code. Choose a method to try again.");
            }
        }

        private async Task<LinkStepResult> ChooseAsync(Guid userId, State state, string? method, CancellationToken ct)
        {
            if (method is not ("push" or "otp") || state.Methods?.Contains(method) != true)
                return MethodSelect(state, "Choose one of the offered methods.");

            var result = await api.ChooseMethodAsync(userId, state.SignInId!, method, ct);
            switch (result.Status)
            {
                case "push_pending":
                    var next = state with { Number = result.Number, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(result.ExpiresInSeconds ?? 60) };
                    return new LinkStepResult(PushForm(next, null), Write(next));
                case "working":
                    // The number is not known yet: show the step and let the poll fill it in.
                    var waiting = state with { Number = null, ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(60) };
                    return method == "push" ? new LinkStepResult(PushForm(waiting, null), Write(waiting)) : new LinkStepResult(OtpForm(state, null), Write(state));
                case "otp_required":
                    return new LinkStepResult(OtpForm(state, null), Write(state));
                case "complete":
                    return await CompleteAsync(userId, result, null, ct);
                default:
                    if (result.ErrorCode == "rate_limited")
                        return MethodSelect(state, RateLimitedMessage);
                    return Terminal(result) ?? MethodSelect(state, "That method did not work. Choose another one.");
            }
        }

        private async Task<LinkStepResult> CompleteAsync(Guid userId, PasswordSignInState result, string? cookiesJson, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.Email))
                return LinkStepResult.Fail("The Microsoft sign-in failed.");
            var account = await linker.SaveAsync(userId, result.MicrosoftUserId, result.Email, result.Name, result.RefreshToken, cookiesJson, ct);
            return LinkStepResult.Done(account.Id.ToString(), account.DisplayName ?? account.Email);
        }

        private async Task<LinkStepResult> BrowserRedirectAsync(string? error, CancellationToken ct)
        {
            BrowserStart start;
            try { start = await api.StartBrowserLoginAsync(ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
            {
                logger.LogWarning("Microsoft browser sign-in start failed ({ExceptionType})", ex.GetType().Name);
                return LinkStepResult.Fail("Microsoft could not be reached. Try again later.");
            }
            var step = new LinkStep
            {
                Id = BrowserStep,
                Kind = LinkStepKind.Redirect,
                Title = "Sign in to Microsoft",
                Error = error,
                Redirect = new LinkRedirect(start.AuthorizeUrl, LinkRedirectMode.Capture, start.RedirectUri, MicrosoftSession.SignInDomains),
            };
            // The PKCE verifier is not a user secret; the host encrypts the state.
            return new LinkStepResult(step, Write(new State(null, null, null, null, start.CodeVerifier)));
        }

        private async Task<LinkStepResult> CompleteBrowserAsync(LinkSubmitContext context, CancellationToken ct)
        {
            var state = Read(context.State);
            if (state?.CodeVerifier is null)
                return LinkStepResult.Fail(ExpiredMessage);

            context.Values.TryGetValue("callbackUrl", out var callbackUrl);
            var code = CodeFrom(callbackUrl);
            if (code is null)
                return await BrowserRedirectAsync("The sign-in didn't finish. Try again.", ct);

            SignInResult result;
            try { result = await api.CompleteBrowserLoginAsync(code, state.CodeVerifier, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                logger.LogWarning("Microsoft browser sign-in completion failed ({ExceptionType})", ex.GetType().Name);
                return await BrowserRedirectAsync("Microsoft could not be reached. Try again.", ct);
            }
            if (result.Status != "complete" || string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.Email))
                return await BrowserRedirectAsync(result.Message ?? "The Microsoft sign-in failed.", ct);

            string? cookiesJson = null;
            if (context.Values.TryGetValue("cookies", out var cookies) && !string.IsNullOrWhiteSpace(cookies))
            {
                try { cookiesJson = MicrosoftSession.ToStored(JsonSerializer.Deserialize<List<MicrosoftSession.Cookie>>(cookies, new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
                catch (JsonException) { }
            }

            var account = await linker.SaveAsync(context.ApplicationUserId, result.MicrosoftUserId, result.Email, result.Name, result.RefreshToken, cookiesJson, ct);
            return LinkStepResult.Done(account.Id.ToString(), account.DisplayName ?? account.Email);
        }

        private static string? CodeFrom(string? callbackUrl)
        {
            if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)) return null;
            var code = HttpUtility.ParseQueryString(uri.Query)["code"];
            return string.IsNullOrEmpty(code) ? null : code;
        }

        private async Task CancelAsync(Guid userId, string? stateJson)
        {
            if (Read(stateJson)?.SignInId is { } id)
                await CancelQuietlyAsync(userId, id);
        }

        private async Task CancelQuietlyAsync(Guid userId, string signInId)
        {
            try { await api.CancelPasswordAsync(userId, signInId, CancellationToken.None); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException) { }
        }

        /// <summary>Terminal error codes of the service as fixed texts (service messages are never surfaced); null when the caller should stay on its step.</summary>
        private static LinkStepResult? Terminal(PasswordSignInState result) => result.ErrorCode switch
        {
            "unsupported" => LinkStepResult.Fail(Unsupported),
            "blocked" => LinkStepResult.Fail(BlockedMessage),
            "otp_exhausted" => LinkStepResult.Fail(ExhaustedMessage),
            "expired" => LinkStepResult.Fail(ExpiredMessage),
            "failed" or "unavailable" => LinkStepResult.Fail(FailedMessage),
            _ => null,
        };

        private static State WithMethods(State state, IReadOnlyList<string> offered)
        {
            var methods = Supported(offered);
            return methods.Count == 0 ? state : state with { Methods = methods };
        }

        private static List<string> Supported(IReadOnlyList<string> offered) =>
            new[] { "push", "otp" }.Where(m => offered.Contains(m)).ToList();

        private static LinkStep CredentialsForm(string? email, string? error) => new()
        {
            Id = CredentialsStep,
            Kind = LinkStepKind.Form,
            Title = "Sign in to Microsoft",
            Error = error,
            SubmitLabel = "Sign in",
            Fields =
            [
                new LinkField { Key = "email", Label = "Email", Type = LinkFieldType.Email, DefaultValue = email },
                new LinkField { Key = "password", Label = "Password", Type = LinkFieldType.Password, Secret = true },
            ],
        };

        private static LinkStepResult MethodSelect(State state, string? error)
        {
            var options = new List<LinkOption>();
            foreach (var method in state.Methods ?? [])
                options.Add(method == "push" ? new LinkOption("push", "Approve in Microsoft Authenticator") : new LinkOption("otp", "Enter a code from your authenticator app"));
            var step = new LinkStep { Id = MethodStep, Kind = LinkStepKind.Select, Title = "Verify it is you", Error = error, Options = options, SubmitLabel = "Continue" };
            return new LinkStepResult(step, Write(state with { Number = null, ExpiresAt = null }));
        }

        private static LinkStep PushForm(State state, string? error) => new()
        {
            Id = PushStep,
            Kind = LinkStepKind.PushCode,
            Title = "Approve the sign-in",
            Description = "Open Microsoft Authenticator and approve the request.",
            Error = error,
            PushCode = new LinkPushCode(state.Number, state.ExpiresAt ?? DateTimeOffset.UtcNow.AddMinutes(1), PollIntervalSeconds),
            Actions = state.Methods?.Contains("otp") == true ? [new LinkAction(UseOtpAction, "Enter a code instead")] : [],
        };

        private static LinkStep OtpForm(State state, string? error) => new()
        {
            Id = OtpStep,
            Kind = LinkStepKind.Otp,
            Title = "Enter your code",
            Error = error,
            Otp = new LinkOtp(6, "Authenticator code"),
            SubmitLabel = "Verify",
            Actions = state.Methods?.Contains("push") == true ? [new LinkAction(UsePushAction, "Approve in the app instead")] : [],
        };

        private static string Write(State state) => JsonSerializer.Serialize(state);

        private static State? Read(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<State>(json); }
            catch (JsonException) { return null; }
        }
    }
}
