using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public class TokenRefreshService(IHttpClientFactory httpClientFactory, SchulwareDbContext db, ILinkedAccounts linkedAccounts, ILogger<TokenRefreshService> logger)
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
                    new KeyValuePair<string, string>("device_id", account.DeviceId ?? ""),
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
            var linkedSession = account.LinkedAccountId is { } linkedId
                ? await linkedAccounts.GetSecretAsync(account.ApplicationUserId, linkedId, LinkedSecrets.SessionCookies, ct)
                : null;
            var sessions = new[] { account.SessionCookiesJson, linkedSession }
                .Where(json => !string.IsNullOrWhiteSpace(json))
                .Distinct()
                .ToList();
            if (sessions.Count == 0)
            {
                logger.LogWarning("Account {AccountId} has no stored session cookies. " +
                    "User must sign in again to seed them.", account.Id);
                return false;
            }

            foreach (var session in sessions)
            {
                var result = await LoginWithSessionAsync(account, session!, ct);
                if (result is null)
                    continue;

                if (!string.IsNullOrEmpty(result.AccessToken)) account.MobileAccessToken = result.AccessToken;
                if (!string.IsNullOrEmpty(result.RefreshToken)) account.MobileRefreshToken = result.RefreshToken;
                account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);
                if (!string.IsNullOrEmpty(result.SessionId)) account.WebSessionId = result.SessionId;
                if (!string.IsNullOrEmpty(result.WebSessionUserId)) account.WebSessionUserId = result.WebSessionUserId;
                if (!string.IsNullOrEmpty(result.WebSessionTransId)) account.WebSessionTransId = result.WebSessionTransId;

                account.SessionCookiesJson = SessionCookies.ToJson(result.SessionCookies) ?? session;
                if (account.LinkedAccountId is { } usedLinkedId)
                    await linkedAccounts.SetSecretAsync(account.ApplicationUserId, usedLinkedId, LinkedSecrets.SessionCookies, account.SessionCookiesJson, ct);

                account.UpdatedAt = DateTime.UtcNow;
                db.Accounts.Update(account);
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Refreshed tokens via SchulwareAPI for account {AccountId}. Expires: {Expires}",
                    account.Id, account.MobileTokenExpiresAt);
                return true;
            }

            return false;
        }

        private async Task<LoginResponseDto?> LoginWithSessionAsync(SchulwareAccount account, string sessionJson, CancellationToken ct)
        {
            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl);
                var result = await client.Api.Authenticate.Login.PostAsync(
                    new LoginRequestDto
                    {
                        SchulnetzBaseUrl = account.SchulnetzBaseUrl,
                        UserAgent = account.UserAgent,
                        SessionCookies = SessionCookies.FromJson(sessionJson),
                    },
                    cancellationToken: ct);

                if (result is not null && result.Success == true)
                    return result;
                logger.LogWarning("SchulwareAPI /refresh failed for account {AccountId}: {Message}",
                    account.Id, result?.Message ?? "no response");
                return null;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "SchulwareAPI /refresh error for account {AccountId}", account.Id);
                return null;
            }
        }
    }
}
