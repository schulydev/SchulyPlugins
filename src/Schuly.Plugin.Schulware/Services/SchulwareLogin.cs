using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// <see cref="IPluginLogin"/> for Schulnetz — a headless email + password
    /// (+ optional TOTP) connect via SchulwareAPI's unified
    /// <c>/api/authenticate/login</c> (ms-entrance, no browser). Stores the mobile
    /// tokens, web session and rotated context_state, provisions the school user
    /// and kicks an initial sync. Replaces the old OAuth-webview flow.
    /// </summary>
    public class SchulwareLogin(
        IPluginUserContext userContext,
        SchulwareDbContext db,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        SchoolProvisioningService provisioning,
        AccountSecretStore secretStore,
        SchulwareSyncTask syncTask,
        IServiceProvider services,
        ILogger<SchulwareLogin> logger) : IPluginLogin
    {
        public SchoolSystemDescriptor SchoolSystem => new()
        {
            Key = "schulnetz",
            DisplayName = "Schulnetz",
            LoginMethod = "credentials",
            PrivateAuthStrategy = "token",
            StatelessBasePath = "/api/plugins/schulware/stateless",
            PluginBasePath = "/api/plugins/schulware",
            SortOrder = 0,
            LoginFields =
            [
                new() { Key = "baseUrl",  Label = "Schulnetz URL", Type = "url",      Placeholder = "https://your-schulnetz.example.ch", Required = true },
                new() { Key = "email",    Label = "Email",         Type = "text",     Required = true },
                new() { Key = "password", Label = "Password",      Type = "password", Required = true },
            ],
        };

        public async Task<PluginLoginResult> ConnectAsync(
            IReadOnlyDictionary<string, string> fields,
            string? displayName,
            CancellationToken cancellationToken = default)
        {
            var baseUrl = Field(fields, "baseUrl")?.TrimEnd('/');
            var email = Field(fields, "email");
            var password = Field(fields, "password");
            var totp = Field(fields, "totp");
            // Opt-out: any explicit "false" disables ongoing background refresh.
            var autoRefresh = !string.Equals(Field(fields, "autoRefresh"), "false", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrWhiteSpace(baseUrl)
                || string.IsNullOrWhiteSpace(email)
                || string.IsNullOrWhiteSpace(password))
                return new PluginLoginResult(false, null, "baseUrl, email and password are required");

            var apiBaseUrl = configuration["SchulwareApi:BaseUrl"] ?? "https://schlwr.pianonic.ch";
            var userId = await userContext.GetCurrentUserIdAsync(cancellationToken);

            var account = await db.Accounts.FirstOrDefaultAsync(
                a => a.ApplicationUserId == userId && a.SchulnetzBaseUrl == baseUrl, cancellationToken);
            var isNew = account is null;
            account ??= new SchulwareAccount
            {
                ApplicationUserId = userId,
                SchulnetzBaseUrl = baseUrl,
                SchulwareApiBaseUrl = apiBaseUrl,
                DisplayName = displayName,
            };

            LoginResponseDto? res;
            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl, baseUrl);
                res = await client.Api.Authenticate.Login.PostAsync(new LoginRequestDto
                {
                    SchulnetzBaseUrl = baseUrl,
                    Email = email,
                    Password = password,
                    TotpSecret = string.IsNullOrWhiteSpace(totp) ? null : totp,
                }, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Schulnetz login failed");
                return new PluginLoginResult(false, null, $"Login failed: {ex.Message}");
            }

            if (res is null || res.Success != true || string.IsNullOrEmpty(res.AccessToken))
                return new PluginLoginResult(false, null, res?.Message ?? "Login failed");

            account.MobileAccessToken = res.AccessToken;
            account.MobileRefreshToken = res.RefreshToken;
            account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);
            account.WebSessionId = res.SessionId;
            account.WebSessionUserId = res.WebSessionUserId;
            account.WebSessionTransId = res.WebSessionTransId;
            account.SessionCookiesJson = SessionCookies.ToJson(res.SessionCookies) ?? account.SessionCookiesJson;
            if (!string.IsNullOrWhiteSpace(displayName))
                account.DisplayName = displayName;
            account.AutoRefresh = autoRefresh;

            if (isNew) db.Accounts.Add(account);
            await provisioning.EnsureAsync(account, userId);
            account.UpdatedAt = DateTime.UtcNow;
            // Only non-secret metadata is persisted here; the secret fields are
            // [NotMapped] and go to the vault instead.
            await db.SaveChangesAsync(cancellationToken);

            // Seed the vault so the initial sync (which reloads the account and
            // hydrates from the vault) has the secrets to work with.
            secretStore.Save(account);

            // Best-effort initial sync so data lands without waiting for the tick.
            if (account.SchoolUserId is not null && account.MobileAccessToken is not null)
            {
                try { await syncTask.SyncAccountAsync(account.Id, services, cancellationToken); }
                catch (Exception ex) { logger.LogWarning(ex, "Initial sync failed for {AccountId}", account.Id); }
            }

            // Autorefresh off: keep nothing — the one-time sync is done, so drop the
            // secrets back out of the vault. The account won't be background-synced.
            if (!autoRefresh)
                secretStore.Remove(account.Id);

            return new PluginLoginResult(true, account.Id, "Connected");
        }

        private static string? Field(IReadOnlyDictionary<string, string> f, string key)
            => f.TryGetValue(key, out var v) ? v : null;
    }
}
