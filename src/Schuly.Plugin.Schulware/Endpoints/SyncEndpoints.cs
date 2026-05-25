using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Endpoints
{
    internal static class SyncEndpoints
    {
        public static IEndpointRouteBuilder MapSchulwareSync(this IEndpointRouteBuilder endpoints)
        {
            // GET = status (read-only)
            endpoints.MapGet("/api/plugins/schulware/accounts/{accountId:guid}/sync", async (
                Guid accountId,
                IPluginUserContext userContext,
                SchulwareDbContext db) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
                if (account is null) return Results.NotFound();

                var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
                return Results.Ok(new
                {
                    account.Id, account.SchulnetzBaseUrl, account.DisplayName,
                    HasMobileToken = account.MobileAccessToken is not null,
                    HasWebSession = account.WebSessionId is not null,
                    LastSync = syncState?.LastSyncAt,
                    SyncStatus = syncState?.LastSyncStatus,
                    SyncError = syncState?.LastSyncError,
                });
            }).RequireAuthorization();

            // POST = trigger a sync NOW (refreshes token if expired, then pulls
            // grades + absences). Returns the resulting SyncState.
            endpoints.MapPost("/api/plugins/schulware/accounts/{accountId:guid}/sync", async (
                Guid accountId,
                IPluginUserContext userContext,
                SchulwareDbContext db,
                IServiceProvider services,
                IEnumerable<IPluginBackgroundTask> tasks,
                CancellationToken ct) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId, ct);
                if (account is null) return Results.NotFound();

                var syncTask = tasks.OfType<SchulwareSyncTask>().FirstOrDefault()
                    ?? throw new InvalidOperationException("SchulwareSyncTask not registered");
                var result = await syncTask.SyncAccountAsync(accountId, services, ct);

                return Results.Ok(new
                {
                    result.LastSyncAt,
                    result.LastSyncStatus,
                    result.LastSyncError,
                });
            }).RequireAuthorization();

            return endpoints;
        }
    }
}
