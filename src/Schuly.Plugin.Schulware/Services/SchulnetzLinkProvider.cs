using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Links Schulnetz accounts: school, then credentials, a linked Microsoft account or the school's browser sign-in.</summary>
    public class SchulnetzLinkProvider(SchulwareDbContext db, ISchulwareConnector connector, ISchulnetzLoginMethods loginMethods, ILinkedAccounts linkedAccounts, AccountSecretStore secretStore, SchulwareSyncLauncher launcher, PushRegistrationService push) : ILinkProvider
    {
        public const string ProviderId = "schulnetz";
        public const string FlowId = "schulnetz";
        public const string SchoolStep = "school";
        public const string CredentialsStep = "credentials";
        public const string MicrosoftStep = "microsoft-account";
        public const string BrowserStep = "browser";
        public const string LinkNewOption = "link-new";
        public const string MobileOnlyMessage = "This school's sign-in needs the Schuly mobile app.";
        private static readonly TimeSpan NewAccountSyncWindow = TimeSpan.FromMinutes(10);

        private sealed record PluginState(string BaseUrl, string? Browser);

        public LinkProviderDescriptor Descriptor { get; } = new()
        {
            Id = ProviderId,
            DisplayName = "Schulnetz",
            Description = "Grades, agenda and absences from your school's Schulnetz.",
            Icon = new LinkIcon { Lucide = "graduation-cap" },
            SortOrder = 0,
            Flows = [new LinkFlowDescriptor { Id = FlowId, Label = "Schulnetz", Platforms = LinkPlatforms.All }],
            Capabilities =
            [
                new ProviderCapability("absence_reporting", "Absences", "calendar-x", "/api/plugins/schulware/accounts"),
                new ProviderCapability("school_data", "School data", "graduation-cap", null),
            ],
        };

        public async Task<LinkStepResult> StartAsync(LinkStartContext context, CancellationToken cancellationToken = default)
        {
            if (context.FlowId != FlowId)
                return LinkStepResult.Fail("Unknown sign-in flow.");

            if (context.ReauthAccountId is not null)
            {
                if (!Guid.TryParse(context.ReauthAccountId, out var id))
                    return LinkStepResult.Fail("That account could not be found.");
                var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.ApplicationUserId == context.ApplicationUserId, cancellationToken);
                if (account is null)
                    return LinkStepResult.Fail("That account could not be found.");
                return await MethodStepAsync(context.ApplicationUserId, context.Platform, account.SchulnetzBaseUrl, null, cancellationToken);
            }

            return new LinkStepResult(SchoolForm(null, null), null);
        }

        public async Task<LinkStepResult> SubmitAsync(LinkSubmitContext context, CancellationToken cancellationToken = default)
        {
            if (context.Action == LinkStepValidator.CancelAction)
                return LinkStepResult.Fail("The sign-in was cancelled.");

            if (context.StepId == SchoolStep)
            {
                var url = Value(context.Values, "baseUrl")?.Trim().TrimEnd('/');
                if (string.IsNullOrEmpty(url) || !BaseUrlGuard.IsAllowed(url))
                    return new LinkStepResult(SchoolForm(url, "Enter a valid https Schulnetz address."), null);
                return await MethodStepAsync(context.ApplicationUserId, context.Platform, url, null, cancellationToken);
            }

            var state = ReadState(context.State);
            if (state is null)
                return LinkStepResult.Fail("The sign-in expired. Start again.");

            switch (context.StepId)
            {
                case CredentialsStep:
                    {
                        var outcome = await connector.ConnectAsync(context.ApplicationUserId, new SchulwareConnectRequest(state.BaseUrl, Value(context.Values, "email"), Value(context.Values, "password"), Value(context.Values, "totp"), true, null, null, null, null, true), cancellationToken);
                        return outcome.Success ? Done(outcome.AccountId!.Value, state.BaseUrl) : new LinkStepResult(CredentialsForm(outcome.Message), context.State);
                    }
                case MicrosoftStep:
                    {
                        var selected = Value(context.Values, "value");
                        if (context.Action == LinkStepValidator.RefreshAction || selected == LinkNewOption || !Guid.TryParse(selected, out var linkedId))
                            return await MicrosoftSelectAsync(context.ApplicationUserId, state, null, cancellationToken);
                        var outcome = await connector.ConnectAsync(context.ApplicationUserId, new SchulwareConnectRequest(state.BaseUrl, null, null, null, true, null, null, linkedId, null, true), cancellationToken);
                        return outcome.Success ? Done(outcome.AccountId!.Value, state.BaseUrl) : await MicrosoftSelectAsync(context.ApplicationUserId, state, outcome.Message, cancellationToken);
                    }
                case BrowserStep:
                    {
                        var browser = BrowserSignIn.Parse(state.Browser);
                        var code = browser is null ? null : BrowserSignIn.CodeFrom(Value(context.Values, "callbackUrl"), browser);
                        var outcome = code is null
                            ? new SchulwareConnectOutcome(false, null, "The sign-in didn't finish. Try again.", ConnectFailure.BrowserIncomplete)
                            : await connector.ConnectAsync(context.ApplicationUserId, new SchulwareConnectRequest(state.BaseUrl, null, null, null, true, browser, code, null, null, true), cancellationToken);
                        if (outcome.Success)
                            return Done(outcome.AccountId!.Value, state.BaseUrl);
                        return await BrowserRedirectAsync(state.BaseUrl, outcome.Message, cancellationToken);
                    }
                default:
                    return LinkStepResult.Fail("Unexpected step.");
            }
        }

        public Task<LinkStepResult> PollAsync(LinkPollContext context, CancellationToken cancellationToken = default) =>
            Task.FromResult(LinkStepResult.Fail("This step does not poll."));

        public async Task<IReadOnlyList<LinkedAccountSummary>> ListAccountsAsync(Guid applicationUserId, CancellationToken cancellationToken = default)
        {
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.ApplicationUserId == applicationUserId).ToListAsync(cancellationToken);
            var ids = accounts.Select(a => a.Id).ToList();
            var states = await db.SyncStates.AsNoTracking().Where(s => ids.Contains(s.AccountId)).ToListAsync(cancellationToken);
            var result = new List<LinkedAccountSummary>();
            foreach (var account in accounts)
            {
                var sync = states.FirstOrDefault(s => s.AccountId == account.Id);
                var (status, message) = StatusOf(account, sync);
                result.Add(new LinkedAccountSummary(account.Id.ToString(), account.DisplayName ?? HostOf(account.SchulnetzBaseUrl), HostOf(account.SchulnetzBaseUrl), status, new DateTimeOffset(DateTime.SpecifyKind(account.CreatedAt, DateTimeKind.Utc)), message, sync?.LastSyncAt is { } at ? new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc)) : null));
            }
            return result;
        }

        public async Task<bool> UnlinkAsync(Guid applicationUserId, string accountId, CancellationToken cancellationToken = default)
        {
            if (!Guid.TryParse(accountId, out var id))
                return false;
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == id && a.ApplicationUserId == applicationUserId, cancellationToken);
            if (account is null)
                return false;

            secretStore.Hydrate(account);
            await push.RemoveAsync(account, cancellationToken);

            var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == id, cancellationToken);
            if (syncState is not null) db.SyncStates.Remove(syncState);
            db.Accounts.Remove(account);
            await db.SaveChangesAsync(cancellationToken);
            secretStore.Remove(id);
            return true;
        }

        private (LinkedAccountStatus Status, string? Message) StatusOf(SchulwareAccount account, SyncState? sync)
        {
            if (!secretStore.Has(account.Id) || sync?.LastSyncStatus is "NeedsReconnect" or "TokenExpired")
                return (LinkedAccountStatus.Expired, "The sign-in expired. Sign in again.");
            if (launcher.IsRunning(account.Id))
                return (LinkedAccountStatus.Syncing, null);
            if (sync is null)
                return DateTime.UtcNow - account.CreatedAt < NewAccountSyncWindow ? (LinkedAccountStatus.Syncing, null) : (LinkedAccountStatus.Ok, null);
            if (sync.LastSyncStatus == "Failed")
                return (LinkedAccountStatus.Error, sync.LastSyncError ?? "The last sync failed.");
            return (LinkedAccountStatus.Ok, null);
        }

        private async Task<LinkStepResult> MethodStepAsync(Guid userId, LinkPlatform platform, string baseUrl, string? error, CancellationToken ct)
        {
            var state = new PluginState(baseUrl, null);
            switch (await loginMethods.DetectAsync(baseUrl, ct))
            {
                case SchulnetzLoginMethod.Microsoft:
                    return await MicrosoftSelectAsync(userId, state, error, ct);
                case SchulnetzLoginMethod.Browser:
                    return platform == LinkPlatform.Web ? LinkStepResult.Fail(MobileOnlyMessage) : await BrowserRedirectAsync(baseUrl, error, ct);
                default:
                    return new LinkStepResult(CredentialsForm(error), WriteState(state));
            }
        }

        private async Task<LinkStepResult> MicrosoftSelectAsync(Guid userId, PluginState state, string? error, CancellationToken ct)
        {
            var linked = await linkedAccounts.ListAsync(userId, LinkedProviders.Microsoft, ct);
            var options = linked.Select(a => new LinkOption(a.Id.ToString(), a.DisplayName ?? a.Email, a.Email)).ToList();
            options.Add(new LinkOption(LinkNewOption, "Link a new Microsoft account") { LinkProviderId = LinkedProviders.Microsoft });
            var step = new LinkStep
            {
                Id = MicrosoftStep,
                Kind = LinkStepKind.Select,
                Title = "Sign in with Microsoft",
                Description = "This school signs in with Microsoft. Pick a linked account or link a new one.",
                Error = error,
                Options = options,
                Actions = [new LinkAction(LinkStepValidator.RefreshAction, "I linked an account")],
                SubmitLabel = "Continue",
            };
            return new LinkStepResult(step, WriteState(state));
        }

        private async Task<LinkStepResult> BrowserRedirectAsync(string baseUrl, string? error, CancellationToken ct)
        {
            var start = await loginMethods.StartBrowserAsync(baseUrl, ct);
            if (start is null)
                return LinkStepResult.Fail(ApiError.UnreachableMessage);
            var step = new LinkStep
            {
                Id = BrowserStep,
                Kind = LinkStepKind.Redirect,
                Title = "Sign in to your school",
                Error = error,
                Redirect = new LinkRedirect(start.AuthUrl, LinkRedirectMode.Capture, start.CallbackUrl, []),
            };
            return new LinkStepResult(step, WriteState(new PluginState(baseUrl, start.Context)));
        }

        private static LinkStep SchoolForm(string? current, string? error) => new()
        {
            Id = SchoolStep,
            Kind = LinkStepKind.Form,
            Title = "Choose your school",
            Error = error,
            SubmitLabel = "Continue",
            Fields =
            [
                new LinkField
                {
                    Key = "baseUrl",
                    Label = "Schulnetz URL",
                    Type = LinkFieldType.Select,
                    Placeholder = "https://your-schulnetz.example.ch",
                    DefaultValue = current,
                    AllowCustom = true,
                    Options = SchulnetzSchools.All.Select(s => new LinkOption(s.Value, s.Label)).ToList(),
                },
            ],
        };

        private static LinkStep CredentialsForm(string? error) => new()
        {
            Id = CredentialsStep,
            Kind = LinkStepKind.Form,
            Title = "Sign in to Schulnetz",
            Error = error,
            SubmitLabel = "Sign in",
            Fields =
            [
                new LinkField { Key = "email", Label = "Email", Type = LinkFieldType.Email },
                new LinkField { Key = "password", Label = "Password", Type = LinkFieldType.Password, Secret = true },
                new LinkField { Key = "totp", Label = "2FA secret (optional)", Type = LinkFieldType.TotpSecret, Secret = true, Required = false, Help = "The TOTP secret or otpauth:// URI, not a 6-digit code." },
            ],
        };

        private static LinkStepResult Done(Guid accountId, string baseUrl) => LinkStepResult.Done(accountId.ToString(), HostOf(baseUrl));

        private static string HostOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

        private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : null;

        private static string WriteState(PluginState state) => JsonSerializer.Serialize(state);

        private static PluginState? ReadState(string? json)
        {
            if (string.IsNullOrEmpty(json)) return null;
            try { return JsonSerializer.Deserialize<PluginState>(json); }
            catch (JsonException) { return null; }
        }
    }
}
