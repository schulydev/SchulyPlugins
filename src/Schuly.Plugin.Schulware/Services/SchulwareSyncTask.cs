using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulnetz account. Everything the mobile API offers comes from it; the web scrape only adds
    /// past semesters' grades (at most once a day). Schools without the mobile API are synced from the web
    /// pages instead. Each part runs on its own, so one failing part doesn't stop the others.
    /// </summary>
    public class SchulwareSyncTask : IPluginBackgroundTask
    {
        private static readonly TimeSpan HistoryInterval = TimeSpan.FromHours(24);

        public string Name => "Schulware Data Sync";
        public PluginSchedule Schedule => PluginSchedule.Every(TimeSpan.FromMinutes(30));

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<SchulwareSyncTask>>();

            var accounts = await db.Accounts
                .Where(a => a.AutoRefresh && a.SchoolUserId != null)
                .ToListAsync(cancellationToken);

            logger.LogInformation("Syncing {Count} Schulware accounts", accounts.Count);

            foreach (var account in accounts)
            {
                await SyncInternalAsync(scope.ServiceProvider, account, cancellationToken);
            }
        }

        public async Task<SyncState> SyncAccountAsync(Guid accountId, IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId, cancellationToken)
                ?? throw new InvalidOperationException($"Account {accountId} not found");

            return await SyncInternalAsync(scope.ServiceProvider, account, cancellationToken);
        }

        private static async Task<SyncState> SyncInternalAsync(IServiceProvider sp, SchulwareAccount account, CancellationToken ct)
        {
            var db = sp.GetRequiredService<SchulwareDbContext>();
            var secretStore = sp.GetRequiredService<AccountSecretStore>();
            var refresh = sp.GetRequiredService<TokenRefreshService>();
            var provisioning = sp.GetRequiredService<SchoolProvisioningService>();
            var logger = sp.GetRequiredService<ILogger<SchulwareSyncTask>>();

            var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == account.Id, ct);
            if (syncState is null)
            {
                syncState = new SyncState { AccountId = account.Id };
                db.SyncStates.Add(syncState);
            }

            if (!secretStore.Hydrate(account))
                return await FailAsync(db, syncState, "NeedsReconnect", "No stored credentials (the in-memory vault is empty, e.g. after a restart). User needs to reconnect.", ct);

            var run = new SectionRunner(logger, account.Id, ct);
            try
            {
                var useMobile = account.MobileAccessToken is not null && account.MobileApiAvailable != false;
                if (useMobile && TokenExpired(account) && !await refresh.RefreshAsync(account, ct))
                    return await FailAsync(db, syncState, "TokenExpired", "Token expired and refresh failed. User needs to re-authenticate.", ct);

                if (await provisioning.EnsureAsync(account, account.ApplicationUserId) is { } problem && account.SchoolUserId is null)
                    return await FailAsync(db, syncState, "Failed", problem, ct);
                await db.SaveChangesAsync(ct);

                if (useMobile)
                    useMobile = await SyncMobileAsync(sp, account, run, ct);
                if (!useMobile)
                    await SyncWebOnlyAsync(sp, account, run, ct);

                syncState.LastSyncAt = DateTime.UtcNow;
                syncState.LastSyncStatus = run.Status;
                syncState.LastSyncError = run.FirstError;
                syncState.LastSyncDetails = run.Details;
                logger.LogInformation("Synced account {AccountId} ({Url}): {Details}", account.Id, account.SchulnetzBaseUrl, run.Details);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                syncState.LastSyncAt = DateTime.UtcNow;
                syncState.LastSyncStatus = "Failed";
                syncState.LastSyncError = ex.Message;
                syncState.LastSyncDetails = run.Details;
                logger.LogError(ex, "Failed to sync account {AccountId}", account.Id);
            }

            secretStore.Save(account);
            await db.SaveChangesAsync(ct);
            return syncState;
        }

        /// <summary>Returns false when the school turns out not to offer the mobile API, so the caller falls back to the web.</summary>
        private static async Task<bool> SyncMobileAsync(IServiceProvider sp, SchulwareAccount account, SectionRunner run, CancellationToken ct)
        {
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);

            MobileGradesSyncService.Result? grades = null;
            var unsupported = false;
            await run.Section("grades", async () =>
            {
                try
                {
                    grades = await sp.GetRequiredService<MobileGradesSyncService>().SyncAsync(client, account, ct);
                    return $"{grades.Count} from the mobile API";
                }
                catch (ApiException ex) when (ex.ResponseStatusCode is 403 or 404)
                {
                    unsupported = true;
                    return "mobile API not offered by this school, using the web pages";
                }
            });
            if (unsupported)
            {
                account.MobileApiAvailable = false;
                return false;
            }
            account.MobileApiAvailable ??= true;

            var subjects = grades?.SubjectsByCourse ?? new Dictionary<string, string>();
            var graded = grades?.GradedExamIds ?? new HashSet<string>();
            await run.Section("timetable", async () => $"{await sp.GetRequiredService<AgendaSyncService>().SyncAsync(client, account, subjects, graded, ct)} entries");
            await run.Section("absences", async () => $"{await sp.GetRequiredService<MobileAbsencesSyncService>().SyncAsync(client, account, ct)} entries");
            await run.Section("documents", async () => { await sp.GetRequiredService<DocumentsSyncService>().SyncAsync(client, account, ct); return "ok"; });
            await run.Section("holidays", async () => { await sp.GetRequiredService<VacationsSyncService>().SyncAsync(client, account, ct); return "ok"; });

            IReadOnlyList<string> reportSemesters = [];
            await run.Section("reports", async () =>
            {
                reportSemesters = await sp.GetRequiredService<SemesterReportsSyncService>().SyncAsync(client, account, ct);
                return $"{reportSemesters.Count} semesters";
            });

            if (account.HistorySyncedAt is null || account.HistorySyncedAt < DateTime.UtcNow - HistoryInterval)
            {
                var current = Semesters.Current(DateOnly.FromDateTime(DateTime.UtcNow));
                var past = reportSemesters.Where(s => s != current).ToList();
                await run.Section("history", async () =>
                {
                    var result = await ScrapeGradesAsync(sp, account, past.Count > 0 ? past : null, current, ct);
                    account.HistorySyncedAt = DateTime.UtcNow;
                    return $"{result.Count} grades from past semesters";
                }, optional: true);
            }

            try { await sp.GetRequiredService<PushRegistrationService>().EnsureAsync(account, ct); }
            catch (Exception ex) { sp.GetRequiredService<ILogger<SchulwareSyncTask>>().LogWarning(ex, "Push registration failed for {AccountId}", account.Id); }
            return true;
        }

        private static async Task SyncWebOnlyAsync(IServiceProvider sp, SchulwareAccount account, SectionRunner run, CancellationToken ct)
        {
            await run.Section("grades", async () => $"{(await ScrapeGradesAsync(sp, account, null, null, ct)).Count} from the web pages");
            await run.Section("absences", async () =>
            {
                await EnsureWebSessionAsync(sp, account, ct);
                var client = SchulwareApiClientFactory.Create(sp.GetRequiredService<IHttpClientFactory>(), account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);
                await sp.GetRequiredService<AbsencesSyncService>().SyncAsync(client, account, ct);
                return "ok";
            });
        }

        /// <summary>Scrapes grades, signing in for a fresh web session first if needed and once more if the session dies.</summary>
        private static async Task<GradesSyncService.Result> ScrapeGradesAsync(IServiceProvider sp, SchulwareAccount account, IReadOnlyCollection<string>? semesters, string? skipSemester, CancellationToken ct)
        {
            var service = sp.GetRequiredService<GradesSyncService>();
            await EnsureWebSessionAsync(sp, account, ct);
            var client = SchulwareApiClientFactory.Create(sp.GetRequiredService<IHttpClientFactory>(), account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);
            try
            {
                return await service.SyncAsync(client, account, semesters, skipSemester, ct);
            }
            catch (WebSessionExpiredException)
            {
                await EnsureWebSessionAsync(sp, account, ct);
                return await service.SyncAsync(client, account, semesters, skipSemester, ct);
            }
        }

        private static async Task EnsureWebSessionAsync(IServiceProvider sp, SchulwareAccount account, CancellationToken ct)
        {
            if (!string.IsNullOrEmpty(account.WebSessionId)) return;
            await sp.GetRequiredService<TokenRefreshService>().RefreshViaRunnerAsync(account, ct);
            await sp.GetRequiredService<SchulwareDbContext>().SaveChangesAsync(ct);
            if (string.IsNullOrEmpty(account.WebSessionId))
                throw new WebSessionExpiredException("Couldn't open a Schulnetz web session; reconnect the account.");
        }

        private static bool TokenExpired(SchulwareAccount account) =>
            account.MobileTokenExpiresAt.HasValue && account.MobileTokenExpiresAt < DateTime.UtcNow;

        private static async Task<SyncState> FailAsync(SchulwareDbContext db, SyncState state, string status, string error, CancellationToken ct)
        {
            state.LastSyncAt = DateTime.UtcNow;
            state.LastSyncStatus = status;
            state.LastSyncError = error;
            await db.SaveChangesAsync(ct);
            return state;
        }

        /// <summary>Runs sync parts independently and keeps a one-line summary of each.</summary>
        private sealed class SectionRunner(ILogger logger, Guid accountId, CancellationToken ct)
        {
            private readonly List<string> _details = [];
            private int _ok;
            private int _failed;

            public string? FirstError { get; private set; }
            public string Details => string.Join("; ", _details);
            public string Status => _failed == 0 ? "Success" : _ok == 0 ? "Failed" : "Partial";

            public async Task Section(string name, Func<Task<string>> work, bool optional = false)
            {
                try
                {
                    _details.Add($"{name}: {await work()}");
                    _ok++;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    _details.Add($"{name}: {(optional ? "skipped" : "failed")} ({ex.Message})");
                    logger.LogWarning(ex, "Sync part {Section} failed for account {AccountId}", name, accountId);
                    if (optional) return;
                    _failed++;
                    FirstError ??= $"{name}: {ex.Message}";
                }
            }
        }
    }

    /// <summary>Swiss school semesters: Aug-Jan is the 1st, Feb-Jul the 2nd ("1. 26/27" starts in August 2026).</summary>
    public static class Semesters
    {
        public static string Current(DateOnly today)
        {
            var (start, half) = today.Month >= 8 ? (today.Year, 1) : today.Month == 1 ? (today.Year - 1, 1) : (today.Year - 1, 2);
            return $"{half}. {start % 100:00}/{(start + 1) % 100:00}";
        }
    }
}
