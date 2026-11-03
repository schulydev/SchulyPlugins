using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>Wipes this plugin's data for a deleted Schuly user: their OdA Org accounts, sync state and vaulted credentials. Without the account row the sync task no longer logs in for them.</summary>
    public class OdaOrgUserPurgeHandler(OdaOrgDbContext db, OdaOrgSecretStore secretStore, ILogger<OdaOrgUserPurgeHandler> logger) : IPluginEventHandler<PluginUserPurgeEvent>
    {
        public async Task HandleAsync(PluginUserPurgeEvent command, CancellationToken cancellationToken = default)
        {
            var accounts = await db.Accounts.Where(a => a.ApplicationUserId == command.ApplicationUserId).ToListAsync(cancellationToken);
            if (accounts.Count == 0)
                return;

            // Secrets go first: the vault can't be searched, so a retry only finds them through the account row.
            foreach (var account in accounts)
                secretStore.Remove(account.Id);

            var accountIds = accounts.Select(a => a.Id).ToList();
            db.SyncStates.RemoveRange(await db.SyncStates.Where(s => accountIds.Contains(s.AccountId)).ToListAsync(cancellationToken));
            db.Accounts.RemoveRange(accounts);
            await db.SaveChangesAsync(cancellationToken);

            logger.LogInformation("Purged {Count} OdA Org account(s) for a deleted user", accounts.Count);
        }
    }
}
