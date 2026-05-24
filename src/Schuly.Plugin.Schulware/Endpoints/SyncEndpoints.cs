using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Endpoints
{
    internal static class SyncEndpoints
    {
        public static IEndpointRouteBuilder MapSchulwareSync(this IEndpointRouteBuilder endpoints)
        {
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

            return endpoints;
        }
    }
}
