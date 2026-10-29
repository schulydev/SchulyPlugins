using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>Runs the first sync after a link in the background, in a fresh scope from the plugin's root provider (this singleton receives it).</summary>
    public sealed class OdaOrgSyncLauncher(IServiceProvider root, OdaOrgSyncTask syncTask, ILogger<OdaOrgSyncLauncher> logger)
    {
        private readonly ConcurrentDictionary<Guid, Task> running = new();

        public bool IsRunning(Guid accountId) => running.ContainsKey(accountId);

        public void Launch(Guid accountId)
        {
            running.GetOrAdd(accountId, id => Task.Run(async () =>
            {
                try { await syncTask.SyncAccountAsync(id, root, CancellationToken.None); }
                catch (Exception ex) { logger.LogWarning(ex, "Initial OdAOrg sync failed for {AccountId}", id); }
                finally { running.TryRemove(id, out _); }
            }));
        }
    }

    /// <summary>Links OdA Org accounts: one credentials form, verified against the portal before the account is saved.</summary>
    public class OdaOrgLinkProvider(OdaOrgDbContext db, OdaOrgSecretStore secretStore, OdaOrgScraper scraper, OdaOrgSyncLauncher launcher) : ILinkProvider
    {
        public const string ProviderId = "odaorg";
        public const string FlowId = "credentials";
        public const string CredentialsStep = "credentials";
        public const string DefaultBaseUrl = "https://odaorg.ict-bbag.ch";
        private static readonly TimeSpan NewAccountSyncWindow = TimeSpan.FromMinutes(10);

        public LinkProviderDescriptor Descriptor { get; } = new()
        {
            Id = ProviderId,
            DisplayName = "OdA Org",
            Description = "Grades and course days from the OdA Org portal.",
            Icon = new LinkIcon { Lucide = "book-open" },
            SortOrder = 1,
            Flows = [new LinkFlowDescriptor { Id = FlowId, Label = "OdA Org", Platforms = LinkPlatforms.All }],
            Capabilities = [new ProviderCapability("school_data", "School data", "graduation-cap", null)],
        };

        public async Task<LinkStepResult> StartAsync(LinkStartContext context, CancellationToken cancellationToken = default)
        {
            if (context.FlowId != FlowId)
                return LinkStepResult.Fail("Unknown sign-in flow.");

            string? baseUrl = null;
            if (context.ReauthAccountId is not null)
            {
                var account = Guid.TryParse(context.ReauthAccountId, out var id)
                    ? await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id && a.ApplicationUserId == context.ApplicationUserId, cancellationToken)
                    : null;
                if (account is null)
                    return LinkStepResult.Fail("That account could not be found.");
                baseUrl = account.BaseUrl;
            }

            return new LinkStepResult(Form(baseUrl, null), null);
        }

        public async Task<LinkStepResult> SubmitAsync(LinkSubmitContext context, CancellationToken cancellationToken = default)
        {
            if (context.Action == LinkStepValidator.CancelAction)
                return LinkStepResult.Fail("The sign-in was cancelled.");
            if (context.StepId != CredentialsStep)
                return LinkStepResult.Fail("Unexpected step.");

            var username = Value(context.Values, "username")?.Trim();
            var password = Value(context.Values, "password");
            var baseUrl = Value(context.Values, "baseUrl")?.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl)) baseUrl = DefaultBaseUrl;

            if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
                return new LinkStepResult(Form(baseUrl, "Enter your username and password."), null);
            if (!BaseUrlGuard.IsAllowed(baseUrl))
                return new LinkStepResult(Form(baseUrl, "That portal address is not allowed."), null);

            var ok = await scraper.CheckLoginAsync(baseUrl, username, password, cancellationToken);
            if (ok is null)
                return new LinkStepResult(Form(baseUrl, ApiError.UnreachableMessage), null);
            if (ok == false)
                return new LinkStepResult(Form(baseUrl, ApiError.LoginFailedMessage), null);

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.ApplicationUserId == context.ApplicationUserId && a.BaseUrl == baseUrl, cancellationToken);
            var isNew = account is null;
            account ??= new OdaOrgAccount { ApplicationUserId = context.ApplicationUserId, BaseUrl = baseUrl };
            account.Username = username;
            account.Password = password;
            account.AutoRefresh = true;
            account.UpdatedAt = DateTime.UtcNow;
            if (isNew) db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);

            secretStore.Save(account);
            launcher.Launch(account.Id);

            return LinkStepResult.Done(account.Id.ToString(), account.DisplayName ?? HostOf(baseUrl));
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
                result.Add(new LinkedAccountSummary(account.Id.ToString(), account.DisplayName ?? HostOf(account.BaseUrl), HostOf(account.BaseUrl), status, Utc(account.CreatedAt), message, sync?.LastSyncAt is { } at ? Utc(at) : null));
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

            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == id, cancellationToken);
            if (state is not null) db.SyncStates.Remove(state);
            db.Accounts.Remove(account);
            await db.SaveChangesAsync(cancellationToken);
            secretStore.Remove(id);
            return true;
        }

        private (LinkedAccountStatus Status, string? Message) StatusOf(OdaOrgAccount account, SyncState? sync)
        {
            if (!secretStore.Has(account.Id) || sync?.LastSyncStatus is "NeedsReconnect" or "AuthFailed")
                return (LinkedAccountStatus.Expired, "The sign-in expired. Sign in again.");
            if (launcher.IsRunning(account.Id))
                return (LinkedAccountStatus.Syncing, null);
            if (sync is null)
                return DateTime.UtcNow - account.CreatedAt < NewAccountSyncWindow ? (LinkedAccountStatus.Syncing, null) : (LinkedAccountStatus.Ok, null);
            if (sync.LastSyncStatus == "Failed")
                return (LinkedAccountStatus.Error, sync.LastSyncError ?? "The last sync failed.");
            return (LinkedAccountStatus.Ok, null);
        }

        private static LinkStep Form(string? baseUrl, string? error) => new()
        {
            Id = CredentialsStep,
            Kind = LinkStepKind.Form,
            Title = "Sign in to OdA Org",
            Error = error,
            SubmitLabel = "Sign in",
            Fields =
            [
                new LinkField { Key = "baseUrl", Label = "Portal URL", Type = LinkFieldType.Url, DefaultValue = baseUrl ?? DefaultBaseUrl, Required = false },
                new LinkField { Key = "username", Label = "Username", Type = LinkFieldType.Text },
                new LinkField { Key = "password", Label = "Password", Type = LinkFieldType.Password, Secret = true },
            ],
        };

        private static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private static string HostOf(string baseUrl) => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Host : baseUrl;

        private static string? Value(IReadOnlyDictionary<string, string> values, string key) => values.TryGetValue(key, out var v) ? v : null;
    }
}
