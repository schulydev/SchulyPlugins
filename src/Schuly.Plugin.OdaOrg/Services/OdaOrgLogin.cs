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
            account.UpdatedAt = DateTime.UtcNow;
            if (isNew) db.Accounts.Add(account);
            await db.SaveChangesAsync(cancellationToken);

            // Best-effort initial sync so data lands without waiting for the tick.
            try { await syncTask.SyncAccountAsync(account.Id, services, cancellationToken); }
            catch (Exception ex) { logger.LogWarning(ex, "Initial OdAOrg sync failed for {AccountId}", account.Id); }

            return new PluginLoginResult(true, account.Id, "Connected");
        }

        private static string? Field(IReadOnlyDictionary<string, string> f, string key)
            => f.TryGetValue(key, out var v) ? v : null;
    }
}
