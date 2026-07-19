using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Shared.Provisioning;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Resolves the main-DB School + SchoolUser rows for a Schulware account using
    /// <c>/api/mobile/userInfo</c>. Creates on first connect, back-fills empty fields
    /// on subsequent ones. The School is keyed on the Schulnetz instance URL (shared
    /// <see cref="SchoolProvisioner"/>), not the user-typed display name.
    /// </summary>
    public class SchoolProvisioningService(IHttpClientFactory httpClientFactory, Schuly.Infrastructure.SchulyDbContext mainDb, ILogger<SchoolProvisioningService> logger)
    {
        /// <summary>
        /// Fetches user info, ensures the School + SchoolUser exist, and stamps them
        /// onto the account. Returns null on success (or when there's nothing to do),
        /// or a short human-readable reason when provisioning failed - the caller
        /// surfaces it instead of reporting a successful connect. Details go to the log.
        /// </summary>
        public async Task<string?> EnsureAsync(SchulwareAccount account, Guid userId)
        {
            if (account.MobileAccessToken is null) return null;

            try
            {
                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl,
                    account.SchulnetzBaseUrl, account.MobileAccessToken);

                var info = await client.Api.Mobile.UserInfo.GetAsync();
                if (info is null) return "couldn't fetch your Schulnetz profile";

                var school = await SchoolProvisioner.EnsureSchoolAsync(mainDb, account.SchulnetzBaseUrl, account.DisplayName);
                var schoolUser = await SchoolProvisioner.EnsureSchoolUserAsync(mainDb, school, userId, Map(info));

                account.SchoolUserId ??= schoolUser.Id;
                account.SchulnetzStudentId ??= info.IdNr;
                return null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-provisioning School/SchoolUser failed for {AccountId}", account.Id);
                return "setting up your school profile failed";
            }
        }

        private static ProvisionedUser Map(UserInfoDto info) => new(
            info.FirstName, info.LastName, info.Email,
            Trim(info.EmailPrivate), Trim(info.Phone) ?? Trim(info.Mobile),
            Trim(info.Street), Trim(info.City), Trim(info.Zip),
            ParseDate(info.Birthday), ParseDate(info.EntryDate), ParseDate(info.ExitDate),
            null);

        private static string? Trim(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private static DateOnly? ParseDate(string? raw) =>
            DateOnly.TryParse(raw, out var d) ? d : null;
    }
}
