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

            await RemoveAccountsAsync(accounts, cancellationToken);
            logger.LogInformation("Purged {Count} Schulnetz account(s) for a deleted user", accounts.Count);
        }

        /// <summary>Removes the accounts with everything that belongs to them. Secrets go before the rows: the vault can't be searched, so a retry only finds them through the account row.</summary>
        public async Task RemoveAccountsAsync(IReadOnlyCollection<SchulwareAccount> accounts, CancellationToken cancellationToken)
        {
            var accountIds = accounts.Select(a => a.Id).ToList();
            foreach (var account in accounts)
            {
                secretStore.Hydrate(account);
                try { await push.RemoveAsync(account, cancellationToken); }
                catch (Exception ex) { logger.LogWarning("Failed to remove a push registration during a purge ({ExceptionType})", ex.GetType().Name); }
                secretStore.Remove(account.Id);
            }

            var syncStates = await db.SyncStates.Where(s => accountIds.Contains(s.AccountId)).ToListAsync(cancellationToken);
            db.SyncStates.RemoveRange(syncStates);
            db.Accounts.RemoveRange(accounts);
            await db.SaveChangesAsync(cancellationToken);
        }
    }
}
