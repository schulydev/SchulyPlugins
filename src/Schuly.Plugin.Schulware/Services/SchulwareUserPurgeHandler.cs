using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Wipes this plugin's data for a deleted Schuly user: their Schulnetz accounts, sync state, push registrations and vaulted secrets.</summary>
    public class SchulwareUserPurgeHandler(SchulwareDbContext db, AccountSecretStore secretStore, PushRegistrationService push, ILogger<SchulwareUserPurgeHandler> logger) : IPluginEventHandler<PluginUserPurgeEvent>
    {
        public async Task HandleAsync(PluginUserPurgeEvent command, CancellationToken cancellationToken = default)
        {
            var accounts = await db.Accounts.Where(a => a.ApplicationUserId == command.ApplicationUserId).ToListAsync(cancellationToken);
            if (accounts.Count == 0)
                return;

            var accountIds = accounts.Select(a => a.Id).ToList();
            foreach (var account in accounts)
            {
                secretStore.Hydrate(account);
                try { await push.RemoveAsync(account, cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Failed to remove push registration for account {AccountId} during user purge", account.Id); }
            }

            var syncStates = await db.SyncStates.Where(s => accountIds.Contains(s.AccountId)).ToListAsync(cancellationToken);
            db.SyncStates.RemoveRange(syncStates);
            db.Accounts.RemoveRange(accounts);
            await db.SaveChangesAsync(cancellationToken);

            foreach (var accountId in accountIds)
                secretStore.Remove(accountId);

            logger.LogInformation("Purged {Count} Schulnetz account(s) for a deleted user", accounts.Count);
        }
    }
}
