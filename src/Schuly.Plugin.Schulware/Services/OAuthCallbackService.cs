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
    /// for tokens, persists the WebView-captured web session, delegates
    /// School/SchoolUser provisioning to <see cref="SchoolProvisioningService"/>,
    /// persists the SSO snapshot, and triggers an initial sync.
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
                ApplyWebSession(account, request);
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

        /// <summary>
        /// Persist the PHP web session the device WebView read off the cookie jar
        /// after the school web login (PHPSESSID + id + transid). Server-side code
        /// exchange / Playwright replay both fail (Playwright logs out on every
        /// navigation), so the WebView hands these over directly. All three must
        /// be present; otherwise the scraper pages stay disabled.
        /// </summary>
        private void ApplyWebSession(SchulwareAccount account, OAuthCallbackRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.WebSessionId)
                || string.IsNullOrWhiteSpace(request.WebSessionUserId)
                || string.IsNullOrWhiteSpace(request.WebSessionTransId))
            {
                logger.LogInformation(
                    "No complete web session captured for {AccountId}; scraper pages disabled", account.Id);
                return;
            }

            account.WebSessionId = request.WebSessionId;
            account.WebSessionUserId = request.WebSessionUserId;
            account.WebSessionTransId = request.WebSessionTransId;
            logger.LogInformation("Stored web session for {AccountId} (id={Id}, transid={Transid})",
                account.Id, account.WebSessionUserId, account.WebSessionTransId);
        }
    }
}
