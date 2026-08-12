using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public class TokenRefreshService(IHttpClientFactory httpClientFactory, SchulwareDbContext db, ILogger<TokenRefreshService> logger)
    {
        public async Task<bool> RefreshAsync(SchulwareAccount account, CancellationToken ct)
        {
            if (await TryDirectAsync(account, ct)) return true;
            return await RefreshViaRunnerAsync(account, ct);
        }

        private async Task<bool> TryDirectAsync(SchulwareAccount account, CancellationToken ct)
        {
            try
            {
                using var httpClient = httpClientFactory.CreateClient("Schulware");
                var content = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string, string>("grant_type", "refresh_token"),
                    new KeyValuePair<string, string>("refresh_token", account.MobileRefreshToken!),
                    new KeyValuePair<string, string>("client_id", "ppyybShnMerHdtBQ"),
                });

                var response = await httpClient.PostAsync($"{account.SchulnetzBaseUrl}/token.php", content, ct);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Direct token refresh failed ({Status}), falling back to SchulwareAPI", response.StatusCode);
                    return false;
                }

                var json = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
                account.MobileAccessToken = json.GetProperty("access_token").GetString();
                account.MobileRefreshToken = json.TryGetProperty("refresh_token", out var rt)
                    ? rt.GetString()
                    : account.MobileRefreshToken;
                account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);
                account.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(ct);

                logger.LogInformation("Refreshed token via token.php for account {AccountId}", account.Id);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Direct token refresh error, falling back to SchulwareAPI");
                return false;
            }
        }

        public async Task<bool> RefreshViaRunnerAsync(SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(account.SessionCookiesJson))
            {
                logger.LogWarning("Account {AccountId} has no stored session cookies. " +
                    "User must sign in again to seed them.", account.Id);
                return false;
            }

            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl);
                var result = await client.Api.Authenticate.Login.PostAsync(
                    new Client.Models.LoginRequestDto
                    {
                        SchulnetzBaseUrl = account.SchulnetzBaseUrl,
                        UserAgent = account.UserAgent,
                        SessionCookies = SessionCookies.FromJson(account.SessionCookiesJson),
                    },
                    cancellationToken: ct);

                if (result is null || result.Success != true)
                {
                    logger.LogWarning("SchulwareAPI /refresh failed for account {AccountId}: {Message}",
                        account.Id, result?.Message ?? "no response");
                    return false;
                }

                if (!string.IsNullOrEmpty(result.AccessToken)) account.MobileAccessToken = result.AccessToken;
                if (!string.IsNullOrEmpty(result.RefreshToken)) account.MobileRefreshToken = result.RefreshToken;
                account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);
                if (!string.IsNullOrEmpty(result.SessionId)) account.WebSessionId = result.SessionId;
                if (!string.IsNullOrEmpty(result.WebSessionUserId)) account.WebSessionUserId = result.WebSessionUserId;
                if (!string.IsNullOrEmpty(result.WebSessionTransId)) account.WebSessionTransId = result.WebSessionTransId;

                account.SessionCookiesJson = SessionCookies.ToJson(result.SessionCookies) ?? account.SessionCookiesJson;

                account.UpdatedAt = DateTime.UtcNow;
                db.Accounts.Update(account);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Refreshed tokens via SchulwareAPI for account {AccountId}. Expires: {Expires}",
                    account.Id, account.MobileTokenExpiresAt);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SchulwareAPI /refresh error for account {AccountId}", account.Id);
                return false;
            }
        }
    }
}
