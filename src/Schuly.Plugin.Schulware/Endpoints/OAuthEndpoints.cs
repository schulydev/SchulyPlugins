using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using System.Net.Http.Json;

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
                IHttpClientFactory httpClientFactory,
                Schuly.Infrastructure.SchulyDbContext mainDb,
                IServiceProvider services,
                IEnumerable<Schuly.Plugin.Abstractions.IPluginBackgroundTask> backgroundTasks) =>
            {
                var userId = await userContext.GetCurrentUserIdAsync();
                var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
                if (account is null) return Results.NotFound();

                // All SchulwareAPI calls below go through the Kiota client. The
                // factory attaches the X-Schulnetz-Base-Url header which
                // SchulwareAPI now requires on mobile/oauth/websession endpoints.
                var anonClient = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl);

                // 1. Exchange code for tokens
                Client.Models.MobileCallbackResponseDto? tokenResult;
                try
                {
                    tokenResult = await anonClient.Api.Authenticate.Oauth.Mobile.Callback.PostAsync(
                        new Client.Models.MobileCallbackRequestDto
                        {
                            Code = request.Code,
                            CodeVerifier = request.CodeVerifier,
                            State = request.State,
                        });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest($"OAuth callback failed: {ex.Message}");
                }
                if (tokenResult is null)
                    return Results.BadRequest("Failed to parse token response");

                account.MobileAccessToken = tokenResult.AccessToken;
                account.MobileRefreshToken = tokenResult.RefreshToken;
                account.MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1);

                // Persist the SSO snapshot the app captured. Without these the
                // stateless /api/authenticate/refresh path can't replay the chain.
                if (!string.IsNullOrWhiteSpace(request.ContextState))
                    account.ContextStateJson = request.ContextState;
                if (!string.IsNullOrWhiteSpace(request.UserAgent))
                    account.UserAgent = request.UserAgent;

                // 2. Capture web session
                try
                {
                    var sessionResult = await anonClient.Api.Websession.Capture.PostAsync(
                        new Client.Models.WebSessionRequestDto
                        {
                            Code = request.Code,
                            State = request.State ?? string.Empty,
                        });
                    if (sessionResult?.Success == true)
                    {
                        account.WebSessionId = sessionResult.SessionId;
                        if (sessionResult.SessionInfo?.AdditionalData is { } info)
                        {
                            account.WebSessionUserId = info.TryGetValue("id", out var idVal) ? idVal?.ToString() : null;
                            account.WebSessionTransId = info.TryGetValue("transid", out var tidVal) ? tidVal?.ToString() : null;
                        }
                    }
                }
                catch
                {
                    // Web session capture is best-effort.
                }

                // 3. Fetch user info and auto-provision School + SchoolUser
                if (account.SchoolUserId is null && account.MobileAccessToken is not null)
                {
                    try
                    {
                        var authedClient = SchulwareApiClientFactory.Create(
                            httpClientFactory, account.SchulwareApiBaseUrl,
                            account.SchulnetzBaseUrl, account.MobileAccessToken);

                        var userInfo = await authedClient.Api.Mobile.UserInfo.GetAsync();
                        if (userInfo is not null)
                        {
                            var schoolName = account.DisplayName ?? account.SchulnetzBaseUrl;
                            var school = await mainDb.Schools.FirstOrDefaultAsync(s => s.Name == schoolName);
                            if (school is null)
                            {
                                // Store the Schulnetz URL on the School so the main DB has
                                // the canonical link too, not just the plugin's account row.
                                school = new Schuly.Domain.School
                                {
                                    Name = schoolName,
                                    Website = account.SchulnetzBaseUrl,
                                };
                                mainDb.Schools.Add(school);
                                await mainDb.SaveChangesAsync();
                            }
                            else if (string.IsNullOrWhiteSpace(school.Website))
                            {
                                school.Website = account.SchulnetzBaseUrl;
                                await mainDb.SaveChangesAsync();
                            }

                            var firstName = userInfo.FirstName ?? "";
                            var lastName = userInfo.LastName ?? "";
                            var email = userInfo.Email ?? "";
                            var studentId = userInfo.IdNr;
                            var birthday = userInfo.Birthday;
                            var entryDate = userInfo.EntryDate;

                            var schoolUser = await mainDb.SchoolUsers
                                .FirstOrDefaultAsync(su => su.ApplicationUserId == userId && su.SchoolId == school.Id);

                            if (schoolUser is null)
                            {
                                schoolUser = new Schuly.Domain.SchoolUser
                                {
                                    ApplicationUserId = userId,
                                    SchoolId = school.Id,
                                    FirstName = firstName,
                                    LastName = lastName,
                                    Email = email,
                                    Birthday = DateOnly.TryParse(birthday, out var bd2) ? bd2 : DateOnly.FromDateTime(DateTime.UtcNow),
                                    EntryDate = DateOnly.TryParse(entryDate, out var ed2) ? ed2 : DateOnly.FromDateTime(DateTime.UtcNow),
                                    Role = Schuly.Domain.Enums.Roles.Student,
                                };
                                mainDb.SchoolUsers.Add(schoolUser);
                                await mainDb.SaveChangesAsync();
                            }

                            account.SchoolUserId = schoolUser.Id;
                            account.SchulnetzStudentId = studentId;
                        }
                    }
                    catch { }
                }

                account.UpdatedAt = DateTime.UtcNow;
                await db.SaveChangesAsync();

                // Kick off an initial sync so the user doesn't have to wait for the
                // 30-min background tick to see grades/absences appear. Failures
                // here are non-fatal — the periodic sync will retry.
                string? initialSyncStatus = null;
                string? initialSyncError = null;
                if (account.SchoolUserId is not null && account.MobileAccessToken is not null)
                {
                    try
                    {
                        var syncTask = backgroundTasks
                            .OfType<Schuly.Plugin.Schulware.Services.SchulwareSyncTask>()
                            .FirstOrDefault();
                        if (syncTask is not null)
                        {
                            var syncState = await syncTask.SyncAccountAsync(account.Id, services);
                            initialSyncStatus = syncState.LastSyncStatus;
                            initialSyncError = syncState.LastSyncError;
                        }
                    }
                    catch (Exception ex)
                    {
                        initialSyncError = ex.Message;
                    }
                }

                return Results.Ok(new
                {
                    Success = true,
                    Message = "Authenticated and session captured",
                    InitialSyncStatus = initialSyncStatus,
                    InitialSyncError = initialSyncError,
                });
            }).RequireAuthorization();

            return endpoints;
        }
    }
}
