using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Shared.Provisioning;

namespace Schuly.Plugin.Schulware.Services
{
    public class SchoolProvisioningService(IHttpClientFactory httpClientFactory, Schuly.Infrastructure.SchulyDbContext mainDb, ILogger<SchoolProvisioningService> logger)
    {
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
