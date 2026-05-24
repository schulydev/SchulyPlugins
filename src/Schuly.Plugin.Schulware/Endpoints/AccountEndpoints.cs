using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;

namespace Schuly.Plugin.Schulware.Endpoints
{
    internal static class AccountEndpoints
    {
        public static IEndpointRouteBuilder MapSchulwareAccounts(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/plugins/schulware/accounts", async (
                IPluginUserContext userContext,
                SchulwareDbContext db) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var accounts = await db.Accounts
                    .Where(a => a.ApplicationUserId == userId)
                    .Select(a => new
                    {
                        a.Id, a.SchulnetzBaseUrl, a.DisplayName, a.SchulnetzStudentId,
                        a.SchoolUserId,
                        HasMobileToken = a.MobileAccessToken != null,
                        HasWebSession = a.WebSessionId != null,
                        a.MobileTokenExpiresAt, a.CreatedAt
                    })
                    .ToListAsync();
                return Results.Ok(accounts);
            }).RequireAuthorization();

            endpoints.MapPost("/api/plugins/schulware/accounts", async (
                ConnectAccountRequest request,
                IPluginUserContext userContext,
                SchulwareDbContext db) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();

                var exists = await db.Accounts.AnyAsync(a =>
                    a.ApplicationUserId == userId && a.SchulnetzBaseUrl == request.SchulnetzBaseUrl);
                if (exists)
                    return Results.BadRequest("Account for this Schulnetz instance already connected");

                var account = new SchulwareAccount
                {
                    ApplicationUserId = userId,
                    SchulnetzBaseUrl = request.SchulnetzBaseUrl,
                    SchulwareApiBaseUrl = request.SchulwareApiBaseUrl ?? "https://schlwr.pianonic.ch",
                    DisplayName = request.DisplayName,
                    SchoolUserId = request.SchoolUserId,
                };
                db.Accounts.Add(account);
                await db.SaveChangesAsync();

                return Results.Ok(new { account.Id, Message = "Account created. Authenticate next." });
            }).RequireAuthorization();

            endpoints.MapDelete("/api/plugins/schulware/accounts/{accountId:guid}", async (
                Guid accountId,
                IPluginUserContext userContext,
                SchulwareDbContext db) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
                if (account is null) return Results.NotFound();

                var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
                if (syncState is not null) db.SyncStates.Remove(syncState);

                db.Accounts.Remove(account);
                await db.SaveChangesAsync();
                return Results.NoContent();
            }).RequireAuthorization();

            return endpoints;
        }
    }
}
