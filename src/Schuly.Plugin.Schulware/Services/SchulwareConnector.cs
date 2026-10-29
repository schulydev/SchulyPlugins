using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    public class SchulwareConnector(SchulwareDbContext db, IHttpClientFactory httpClientFactory, IConfiguration configuration, SchoolProvisioningService provisioning, AccountSecretStore secretStore, ILinkedAccounts linkedAccounts, SchulwareSyncTask syncTask, SchulwareSyncLauncher launcher, IServiceProvider services, ILogger<SchulwareConnector> logger) : ISchulwareConnector
    {
        public async Task<SchulwareConnectOutcome> ConnectAsync(Guid userId, SchulwareConnectRequest request, CancellationToken cancellationToken = default)
        {
            var baseUrl = request.BaseUrl?.TrimEnd('/');
            var browser = request.Browser;
            var browserCode = request.BrowserCode;
            var linkedAccountId = request.LinkedAccountId;
            var displayName = request.DisplayName;
            var autoRefresh = request.AutoRefresh;

            if (string.IsNullOrWhiteSpace(baseUrl))
                return Fail(ConnectFailure.Invalid, "baseUrl is required");
            if (browser is not null && browserCode is null)
                return Fail(ConnectFailure.BrowserIncomplete, "The sign-in didn't finish. Try again.");
            if (linkedAccountId is null && browser is null && (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.Password)))
                return Fail(ConnectFailure.Invalid, "email and password, or a linked Microsoft account, are required");

            if (!BaseUrlGuard.IsAllowed(baseUrl))
                return Fail(ConnectFailure.Invalid, "baseUrl is not an allowed target");

            var apiBaseUrl = configuration["SchulwareApi:BaseUrl"] ?? "https://schlwr.pianonic.ch";

            List<LoginRequestDto_session_cookies>? linkedSession = null;
            if (linkedAccountId is { } linkedId)
            {
                var linked = await linkedAccounts.GetAsync(userId, linkedId, cancellationToken);
                if (linked is null || linked.Provider != LinkedProviders.Microsoft)
                    return Fail(ConnectFailure.Invalid, "The selected Microsoft account is not linked to your Schuly account");
                linkedSession = SessionCookies.FromJson(await linkedAccounts.GetSecretAsync(userId, linkedId, LinkedSecrets.SessionCookies, cancellationToken));
                if (linkedSession is null || linkedSession.Count == 0)
                    return Fail(ConnectFailure.SignInExpired, "The Microsoft sign-in of that account has expired. Link it again in Settings.");
            }

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
                res = browser is not null
                    ? await client.Api.Authenticate.Browser.Complete.PostAsync(new BrowserLoginCompleteRequestDto { SchulnetzBaseUrl = baseUrl, Code = browserCode, CodeVerifier = browser.CodeVerifier, DeviceId = browser.DeviceId }, cancellationToken: cancellationToken)
                    : await client.Api.Authenticate.Login.PostAsync(linkedSession is not null
                    ? new LoginRequestDto { SchulnetzBaseUrl = baseUrl, SessionCookies = linkedSession }
                    : new LoginRequestDto
                    {
                        SchulnetzBaseUrl = baseUrl,
                        Email = request.Email,
                        Password = request.Password,
                        TotpSecret = string.IsNullOrWhiteSpace(request.Totp) ? null : request.Totp,
                    }, cancellationToken: cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Schulnetz login failed");
                return Fail(ConnectFailure.Unreachable, ApiError.UnreachableMessage);
            }

            if (res is null || res.Success != true || string.IsNullOrEmpty(res.AccessToken))
                return linkedSession is not null
                    ? Fail(ConnectFailure.SignInExpired, "The Microsoft sign-in of that account has expired. Link it again in Settings.")
                    : Fail(ConnectFailure.Credentials, ApiError.LoginFailedMessage);

            account.LinkedAccountId = linkedAccountId;
            account.DeviceId = browser?.DeviceId;
            if (linkedAccountId is { } usedLinkedId && SessionCookies.ToJson(res.SessionCookies) is { } rotated)
                await linkedAccounts.SetSecretAsync(userId, usedLinkedId, LinkedSecrets.SessionCookies, rotated, cancellationToken);

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

            // Provisioning must succeed for the account to be usable (it mints the
            // SchoolUser the app renders). If it fails, don't report a successful
            // connect - roll a newly-added account back so the user can cleanly retry
            // and surface the reason instead of a silent, empty "connected" account.
            var provisioningError = await provisioning.EnsureAsync(account, userId);
            if (provisioningError is not null)
            {
                if (isNew) db.Accounts.Remove(account);
                await db.SaveChangesAsync(cancellationToken);
                return Fail(ConnectFailure.Provisioning, $"Connected to Schulnetz, but {provisioningError}. Please try again.");
            }

            account.UpdatedAt = DateTime.UtcNow;
            // Only non-secret metadata is persisted here; the secret fields are
            // [NotMapped] and go to the vault instead.
            await db.SaveChangesAsync(cancellationToken);

            secretStore.Save(account);

            if (account.SchoolUserId is not null && account.MobileAccessToken is not null)
            {
                if (request.BackgroundSync && autoRefresh)
                {
                    launcher.Launch(account.Id);
                }
                else
                {
                    try { await syncTask.SyncAccountAsync(account.Id, services, cancellationToken); }
                    catch (Exception ex) { logger.LogWarning(ex, "Initial sync failed for {AccountId}", account.Id); }
                }
            }

            if (!autoRefresh)
                secretStore.Remove(account.Id);

            return new SchulwareConnectOutcome(true, account.Id, "Connected", ConnectFailure.None);
        }

        private static SchulwareConnectOutcome Fail(ConnectFailure failure, string message) => new(false, null, message, failure);
    }
}
