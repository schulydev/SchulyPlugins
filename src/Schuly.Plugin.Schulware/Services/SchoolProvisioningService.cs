using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Resolves the main-DB <see cref="School"/> + <see cref="SchoolUser"/>
    /// rows for a Schulware account using <c>/api/mobile/userInfo</c>.
    /// Creates on first connect, back-fills empty fields on subsequent ones.
    /// </summary>
    public class SchoolProvisioningService(
        IHttpClientFactory httpClientFactory,
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<SchoolProvisioningService> logger)
    {
        /// <summary>
        /// Best-effort: fetch user info, ensure School + SchoolUser exist,
        /// stamp them onto the account. Failures are non-fatal — the caller
        /// can still complete the OAuth flow without provisioning.
        /// </summary>
        public async Task EnsureAsync(SchulwareAccount account, Guid userId)
        {
            if (account.MobileAccessToken is null) return;

            try
            {
                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl,
                    account.SchulnetzBaseUrl, account.MobileAccessToken);

                var info = await client.Api.Mobile.UserInfo.GetAsync();
                if (info is null) return;

                var school = await GetOrCreateSchoolAsync(account);
                var schoolUser = await GetOrCreateSchoolUserAsync(school, userId, info);

                // Stamp the account on first connect; ignore on subsequent
                // calls since the IDs don't change.
                account.SchoolUserId ??= schoolUser.Id;
                account.SchulnetzStudentId ??= info.IdNr;
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
            if (existing is not null)
            {
                ApplyUserInfo(existing, info);
                await mainDb.SaveChangesAsync();
                return existing;
            }

            var schoolUser = new SchoolUser
            {
                ApplicationUserId = userId,
                SchoolId = school.Id,
                FirstName = info.FirstName ?? "",
                LastName = info.LastName ?? "",
                Email = info.Email ?? "",
                Birthday = ParseDate(info.Birthday) ?? DateOnly.FromDateTime(DateTime.UtcNow),
                EntryDate = ParseDate(info.EntryDate) ?? DateOnly.FromDateTime(DateTime.UtcNow),
                Role = Schuly.Domain.Enums.Roles.Student,
            };
            ApplyUserInfo(schoolUser, info);
            mainDb.SchoolUsers.Add(schoolUser);
            await mainDb.SaveChangesAsync();
            return schoolUser;
        }

        /// <summary>
        /// Copy contact + address info from Schulnetz to the SchoolUser. Only
        /// overwrites blanks so manual edits aren't clobbered on later logins.
        /// </summary>
        private static void ApplyUserInfo(SchoolUser user, UserInfoDto info)
        {
            user.PrivateEmail ??= Trim(info.EmailPrivate);
            user.PhoneNumber ??= Trim(info.Phone) ?? Trim(info.Mobile);
            user.Street ??= Trim(info.Street);
            user.City ??= Trim(info.City);
            user.Zip ??= Trim(info.Zip);
            user.LeaveDate ??= ParseDate(info.ExitDate);
        }

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateOnly? ParseDate(string? raw) =>
            DateOnly.TryParse(raw, out var d) ? d : null;
    }
}
