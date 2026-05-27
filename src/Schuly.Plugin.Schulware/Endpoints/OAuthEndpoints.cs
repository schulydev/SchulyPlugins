using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Endpoints
{
    internal static class OAuthEndpoints
    {
        public static IEndpointRouteBuilder MapSchulwareOAuth(this IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/plugins/schulware/accounts/{accountId:guid}/auth/oauth/url", async (
                Guid accountId,
                IPluginUserContext userContext,
                SchulwareDbContext db,
                IHttpClientFactory httpClientFactory) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
                if (account is null) return Results.NotFound();

                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl);
                var result = await client.Api.Authenticate.Oauth.Mobile.Url.GetAsync();
                return Results.Ok(result);
            }).RequireAuthorization();

            endpoints.MapPost("/api/plugins/schulware/accounts/{accountId:guid}/auth/oauth/callback", async (
                Guid accountId,
                OAuthCallbackRequest request,
                IPluginUserContext userContext,
                SchulwareDbContext db,
                OAuthCallbackService callbackService,
                IServiceProvider services) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
                if (account is null) return Results.NotFound();

                var result = await callbackService.HandleAsync(account, userId, request, services);
                if (!result.Success) return Results.BadRequest(result.Error);

                return Results.Ok(new
                {
                    Success = true,
                    Message = "Authenticated and session captured",
                    InitialSyncStatus = result.InitialSyncStatus,
                    InitialSyncError = result.InitialSyncError,
                });
            }).RequireAuthorization();

            return endpoints;
        }
    }
}
