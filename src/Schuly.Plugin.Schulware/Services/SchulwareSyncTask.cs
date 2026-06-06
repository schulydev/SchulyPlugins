using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Periodic Schulware sync. Iterates every authenticated account, refreshes
    /// expired tokens, and pulls grades + absences into the main Schuly DB.
    /// The actual work is delegated to <see cref="TokenRefreshService"/>,
    /// <see cref="GradesSyncService"/>, and <see cref="AbsencesSyncService"/>.
    /// </summary>
    public class SchulwareSyncTask : IPluginBackgroundTask
    {
        public string Name => "Schulware Data Sync";
        public TimeSpan Interval => TimeSpan.FromMinutes(30);

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SchulwareSyncTask>>();

            var accounts = await db.Accounts
                .Where(a => a.MobileAccessToken != null && a.SchoolUserId != null)
                .ToListAsync(cancellationToken);

            logger.LogInformation("Syncing {Count} Schulware accounts", accounts.Count);

            foreach (var account in accounts)
            {
                await SyncInternalAsync(scope.ServiceProvider, account, cancellationToken);
            }
        }

        /// <summary>
        /// Sync one account on demand. Same logic as the periodic loop. Returns
        /// the persisted SyncState so callers can surface status/error.
        /// </summary>
        public async Task<SyncState> SyncAccountAsync(
            Guid accountId, IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
                ?? throw new InvalidOperationException($"Account {accountId} not found");

            return await SyncInternalAsync(scope.ServiceProvider, account, cancellationToken);
        }

        private static async Task<SyncState> SyncInternalAsync(
            IServiceProvider scopedSp, SchulwareAccount account, CancellationToken ct)
        {
            var db = scopedSp.GetRequiredService<SchulwareDbContext>();
            var refresh = scopedSp.GetRequiredService<TokenRefreshService>();
            var provisioning = scopedSp.GetRequiredService<SchoolProvisioningService>();
            var grades = scopedSp.GetRequiredService<GradesSyncService>();
            var absences = scopedSp.GetRequiredService<AbsencesSyncService>();
            var agenda = scopedSp.GetRequiredService<AgendaSyncService>();
            var documents = scopedSp.GetRequiredService<DocumentsSyncService>();
            var httpClientFactory = scopedSp.GetRequiredService<IHttpClientFactory>();
            var logger = scopedSp.GetRequiredService<ILogger<SchulwareSyncTask>>();

            var syncState = await db.SyncStates
                .FirstOrDefaultAsync(s => s.AccountId == account.Id, ct);
            if (syncState is null)
            {
                syncState = new SyncState { AccountId = account.Id };
                db.SyncStates.Add(syncState);
            }

            try
            {
                if (TokenExpired(account))
                {
                    if (account.MobileRefreshToken is null)
                        return await FailAsync(db, syncState, "TokenExpired",
                            "Token expired. No refresh token available. User needs to re-authenticate.", ct);

                    if (!await refresh.RefreshAsync(account, ct))
                        return await FailAsync(db, syncState, "TokenExpired",
                            "Token expired and refresh failed. User needs to re-authenticate.", ct);
                }

                // Pick up newly-exposed profile fields (PrivateEmail, City…)
                // on every sync, not only first connect. EnsureAsync only
                // overwrites blanks, so manual edits stay intact.
                await provisioning.EnsureAsync(account, account.ApplicationUserId);
                await db.SaveChangesAsync(ct);

                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl,
                    account.SchulnetzBaseUrl, account.MobileAccessToken);

                // Grades first so Classes are populated, then agenda (which
                // looks up Class by name) and absences. Documents are scraper-only
                // and no-op without a web session.
                await grades.SyncAsync(client, account, ct);
                await agenda.SyncAsync(client, account, ct);
                await absences.SyncAsync(client, account, ct);
                await documents.SyncAsync(client, account, ct);

                syncState.LastSyncAt = DateTime.UtcNow;
                syncState.LastSyncStatus = "Success";
                syncState.LastSyncError = null;
                logger.LogInformation("Synced account {AccountId} ({Url})", account.Id, account.SchulnetzBaseUrl);
            }
            catch (Exception ex)
            {
                syncState.LastSyncAt = DateTime.UtcNow;
                syncState.LastSyncStatus = "Failed";
                syncState.LastSyncError = ex.Message;
                logger.LogError(ex, "Failed to sync account {AccountId}", account.Id);
            }

            await db.SaveChangesAsync(ct);
            return syncState;
        }

        private static bool TokenExpired(SchulwareAccount account) =>
            account.MobileTokenExpiresAt.HasValue && account.MobileTokenExpiresAt < DateTime.UtcNow;

        private static async Task<SyncState> FailAsync(
            SchulwareDbContext db, SyncState state, string status, string error, CancellationToken ct)
        {
            state.LastSyncAt = DateTime.UtcNow;
            state.LastSyncStatus = status;
            state.LastSyncError = error;
            await db.SaveChangesAsync(ct);
            return state;
        }
    }
}
