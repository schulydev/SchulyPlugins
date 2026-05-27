using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public record OAuthCallbackResult(
        bool Success,
        string? Error,
        string? InitialSyncStatus,
        string? InitialSyncError);

    /// <summary>
    /// Drives the back half of the Schulnetz OAuth login: exchanges the code
    /// for tokens, captures the web session, delegates School/SchoolUser
    /// provisioning to <see cref="SchoolProvisioningService"/>, persists the
    /// SSO snapshot, and triggers an initial sync.
    /// </summary>
    public class OAuthCallbackService(
        IHttpClientFactory httpClientFactory,
        SchulwareDbContext db,
        SchulwareSyncTask syncTask,
        SchoolProvisioningService provisioning,
        ILogger<OAuthCallbackService> logger)
    {
        public async Task<OAuthCallbackResult> HandleAsync(
            SchulwareAccount account, Guid userId, OAuthCallbackRequest request, IServiceProvider services)
        {
            var anonClient = SchulwareApiClientFactory.Create(
                httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl);

            try
            {
                var tokens = await anonClient.Api.Authenticate.Oauth.Mobile.Callback.PostAsync(
                    new MobileCallbackRequestDto
                    {
                        Code = request.Code,
                        CodeVerifier = request.CodeVerifier,
                        State = request.State,
                    });
                if (tokens is null)
                    return new(false, "Failed to parse token response", null, null);

                ApplyTokens(account, tokens, request);
                await CaptureWebSessionAsync(account, anonClient, request);
                await provisioning.EnsureAsync(account, userId);

                account.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();
            }
            catch (Exception ex)
            {
                return new(false, $"OAuth callback failed: {ex.Message}", null, null);
            }

            // Kick off an initial sync so the user doesn't wait for the periodic
            // tick. Failures are non-fatal — the background loop will retry.
            string? syncStatus = null;
            string? syncError = null;
            if (account.SchoolUserId is not null && account.MobileAccessToken is not null)
            {
                try
                {
                    var state = await syncTask.SyncAccountAsync(account.Id, services);
                    syncStatus = state.LastSyncStatus;
                    syncError = state.LastSyncError;
                }
                catch (Exception ex)
                {
                    syncError = ex.Message;
                }
            }

            return new(true, null, syncStatus, syncError);
        }

        private static void ApplyTokens(SchulwareAccount account, MobileCallbackResponseDto tokens, OAuthCallbackRequest request)
        {
            account.MobileAccessToken = tokens.AccessToken;
            account.MobileRefreshToken = tokens.RefreshToken;
            account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);

            // Persist the SSO snapshot the app captured. Without these the
            // stateless /api/authenticate/refresh path can't replay the chain.
            if (!string.IsNullOrWhiteSpace(request.ContextState))
                account.ContextStateJson = request.ContextState;
            if (!string.IsNullOrWhiteSpace(request.UserAgent))
                account.UserAgent = request.UserAgent;
        }

        private async Task CaptureWebSessionAsync(SchulwareAccount account, Client.SchulwareApiClient anonClient, OAuthCallbackRequest request)
        {
            try
            {
                var session = await anonClient.Api.Websession.Capture.PostAsync(
                    new WebSessionRequestDto
                    {
                        Code = request.Code,
                        State = request.State ?? string.Empty,
                    });
                if (session?.Success != true) return;

                account.WebSessionId = session.SessionId;
                if (session.SessionInfo?.AdditionalData is { } info)
                {
                    account.WebSessionUserId = info.TryGetValue("id", out var idVal) ? idVal?.ToString() : null;
                    account.WebSessionTransId = info.TryGetValue("transid", out var tidVal) ? tidVal?.ToString() : null;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Web session capture failed (best-effort) for {AccountId}", account.Id);
            }
        }
    }
}
