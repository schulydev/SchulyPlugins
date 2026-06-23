using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// <see cref="IPluginLogin"/> for OdAOrg — a username + password connect.
    /// Stores the credentials (OdAOrg has no token; the scraper replays them) and
    /// kicks an initial sync.
    /// </summary>
    public class OdaOrgLogin(
        IPluginUserContext userContext,
        OdaOrgDbContext db,
        OdaOrgSecretStore secretStore,
        OdaOrgSyncTask syncTask,
        IServiceProvider services,
        ILogger<OdaOrgLogin> logger) : IPluginLogin
    {
        public string SystemKey => "odaorg";

        public async Task<PluginLoginResult> ConnectAsync(
            IReadOnlyDictionary<string, string> fields,
            string? displayName,
            CancellationToken cancellationToken = default)
        {
            var username = Field(fields, "username");
            var password = Field(fields, "password");
            var baseUrl = Field(fields, "baseUrl");
            // Opt-out: any explicit "false" disables ongoing background sync.
            var autoRefresh = !string.Equals(Field(fields, "autoRefresh"), "false", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                return new PluginLoginResult(false, null, "username and password are required");
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "https://odaorg.ict-bbag.ch" : baseUrl.TrimEnd('/');

            var userId = await userContext.GetCurrentUserIdAsync(cancellationToken);
            var account = await db.Accounts.FirstOrDefaultAsync(
                a => a.ApplicationUserId == userId && a.BaseUrl == baseUrl, cancellationToken);
            var isNew = account is null;
            account ??= new OdaOrgAccount
            {
                ApplicationUserId = userId,
                BaseUrl = baseUrl,
                Username = username,
                Password = password,
                DisplayName = displayName,
            };
            account.Username = username;
            account.Password = password;
            if (!string.IsNullOrWhiteSpace(displayName))
                account.DisplayName = displayName;
            account.AutoRefresh = autoRefresh;
            account.UpdatedAt = DateTime.UtcNow;
            if (isNew) db.Accounts.Add(account);
            // Only non-secret metadata is persisted; the credentials are [NotMapped]
            // and go to the vault instead.
            await db.SaveChangesAsync(cancellationToken);

            // Seed the vault so the initial sync (which reloads the account and
            // hydrates from the vault) has the credentials to replay.
            secretStore.Save(account);

            // Best-effort initial sync so data lands without waiting for the tick.
            try { await syncTask.SyncAccountAsync(account.Id, services, cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Initial OdAOrg sync failed for {AccountId}", account.Id); }

            // Autorefresh off: keep nothing — the one-time sync is done, so drop the
            // credentials back out of the vault. The account won't be background-synced.
            if (!autoRefresh)
                secretStore.Remove(account.Id);

            return new PluginLoginResult(true, account.Id, "Connected");
        }

        private static string? Field(IReadOnlyDictionary<string, string> f, string key)
            => f.TryGetValue(key, out var v) ? v : null;
    }
}
