using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Periodic OdaOrg sync (auto-refresh). The host's PluginBackgroundTaskHost
    /// runs <see cref="ExecuteAsync"/> every <see cref="Interval"/>; the same
    /// per-account logic is also exposed via <see cref="SyncAccountAsync"/> for
    /// the on-demand POST endpoint. Mirrors the Schulware plugin's sync task.
    /// </summary>
    public class OdaOrgSyncTask : IPluginBackgroundTask
    {
        public string Name => "OdaOrg Data Sync";
        public TimeSpan Interval => TimeSpan.FromMinutes(30);

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<OdaOrgSyncTask>>();

            var accounts = await db.Accounts.Where(a => a.AutoRefresh).ToListAsync(cancellationToken);
            logger.LogInformation("Auto-refreshing {Count} OdaOrg accounts", accounts.Count);

            foreach (var account in accounts)
                await SyncInternalAsync(scope.ServiceProvider, account, cancellationToken);
        }

        public async Task<SyncState> SyncAccountAsync(Guid accountId, IServiceProvider serviceProvider, CancellationToken ct = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, ct)
                ?? throw new InvalidOperationException($"OdaOrg account {accountId} not found");
            return await SyncInternalAsync(scope.ServiceProvider, account, ct);
        }

        private static async Task<SyncState> SyncInternalAsync(IServiceProvider sp, OdaOrgAccount account, CancellationToken ct)
        {
            var db = sp.GetRequiredService<OdaOrgDbContext>();
            var secretStore = sp.GetRequiredService<OdaOrgSecretStore>();
            var scraper = sp.GetRequiredService<OdaOrgScraper>();
            var provisioning = sp.GetRequiredService<ProvisioningService>();
            var grades = sp.GetRequiredService<GradesSyncService>();
            var agenda = sp.GetRequiredService<AgendaSyncService>();
            var logger = sp.GetRequiredService<ILogger<OdaOrgSyncTask>>();

            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == account.Id, ct);
            if (state is null) { state = new SyncState { AccountId = account.Id }; db.SyncStates.Add(state); }

            try
            {
                // Credentials live in the vault, not the DB — empty after a restart,
                // in which case the user must reconnect to re-seed them.
                if (!secretStore.Hydrate(account))
                {
                    state.LastSyncAt = DateTime.UtcNow;
                    state.LastSyncStatus = "NeedsReconnect";
                    state.LastSyncError = "No stored credentials (the in-memory vault is empty, e.g. after a restart). User needs to reconnect.";
                    await db.SaveChangesAsync(ct);
                    return state;
                }

                var scrape = await scraper.ScrapeAsync(account.BaseUrl, account.Username!, account.Password!, ct);
                if (scrape is null)
                {
                    state.LastSyncAt = DateTime.UtcNow;
                    state.LastSyncStatus = "AuthFailed";
                    state.LastSyncError = "Login failed — check username/password.";
                    await db.SaveChangesAsync(ct);
                    return state;
                }

                await provisioning.EnsureAsync(account, scrape.Profile, ct);
                account.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                await grades.SyncAsync(account, scrape.Grades, ct);
                await agenda.SyncAsync(account, scrape.CourseDays, ct);

                state.LastSyncAt = DateTime.UtcNow;
                state.LastSyncStatus = "Success";
                state.LastSyncError = null;
                logger.LogInformation("Synced OdaOrg account {Account}", account.Id);
            }
            catch (Exception ex)
            {
                state.LastSyncAt = DateTime.UtcNow;
                state.LastSyncStatus = "Failed";
                state.LastSyncError = ex.Message;
                logger.LogError(ex, "Failed to sync OdaOrg account {Account}", account.Id);
            }

            await db.SaveChangesAsync(ct);
            return state;
        }
    }
}
