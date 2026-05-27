using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
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
    /// for tokens, captures the web session, auto-provisions the
    /// <see cref="School"/> + <see cref="SchoolUser"/>, persists the SSO
    /// snapshot, and triggers an initial sync.
    /// </summary>
    public class OAuthCallbackService(
        IHttpClientFactory httpClientFactory,
        SchulwareDbContext db,
        Schuly.Infrastructure.SchulyDbContext mainDb,
        SchulwareSyncTask syncTask,
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
                await EnsureSchoolAndUserAsync(account, userId);

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

        private async Task EnsureSchoolAndUserAsync(SchulwareAccount account, Guid userId)
        {
            if (account.SchoolUserId is not null || account.MobileAccessToken is null) return;

            try
            {
                var authedClient = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl,
                    account.SchulnetzBaseUrl, account.MobileAccessToken);

                var info = await authedClient.Api.Mobile.UserInfo.GetAsync();
                if (info is null) return;

                var school = await GetOrCreateSchoolAsync(account);
                var schoolUser = await GetOrCreateSchoolUserAsync(school, userId, info);

                account.SchoolUserId = schoolUser.Id;
                account.SchulnetzStudentId = info.IdNr;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-provisioning School/SchoolUser failed for {AccountId}", account.Id);
            }
        }

        private async Task<School> GetOrCreateSchoolAsync(SchulwareAccount account)
        {
            var name = account.DisplayName ?? account.SchulnetzBaseUrl;
            var school = await mainDb.Schools.FirstOrDefaultAsync(s => s.Name == name);
            if (school is null)
            {
                // Store the Schulnetz URL on the School so the main DB has
                // the canonical link too, not just the plugin's account row.
                school = new School { Name = name, Website = account.SchulnetzBaseUrl };
                mainDb.Schools.Add(school);
                await mainDb.SaveChangesAsync();
            }
            else if (string.IsNullOrWhiteSpace(school.Website))
            {
                school.Website = account.SchulnetzBaseUrl;
                await mainDb.SaveChangesAsync();
            }
            return school;
        }

        private async Task<SchoolUser> GetOrCreateSchoolUserAsync(School school, Guid userId, UserInfoDto info)
        {
            var existing = await mainDb.SchoolUsers
                .FirstOrDefaultAsync(su => su.ApplicationUserId == userId && su.SchoolId == school.Id);
            if (existing is not null) return existing;

            var schoolUser = new SchoolUser
            {
                ApplicationUserId = userId,
                SchoolId = school.Id,
                FirstName = info.FirstName ?? "",
                LastName = info.LastName ?? "",
                Email = info.Email ?? "",
                Birthday = DateOnly.TryParse(info.Birthday, out var bd) ? bd : DateOnly.FromDateTime(DateTime.UtcNow),
                EntryDate = DateOnly.TryParse(info.EntryDate, out var ed) ? ed : DateOnly.FromDateTime(DateTime.UtcNow),
                Role = Schuly.Domain.Enums.Roles.Student,
            };
            mainDb.SchoolUsers.Add(schoolUser);
            await mainDb.SaveChangesAsync();
            return schoolUser;
        }
    }
}
