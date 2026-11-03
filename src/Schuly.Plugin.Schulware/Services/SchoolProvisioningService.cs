using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Shared.Provisioning;

namespace Schuly.Plugin.Schulware.Services
{
    public class SchoolProvisioningService(IHttpClientFactory httpClientFactory, ISchoolData schoolData, IConfiguration configuration, ILogger<SchoolProvisioningService> logger)
    {
        /// <summary>What <see cref="EnsureAsync"/> returns when the student left longer ago than the host's retention period: nothing may be provisioned again.</summary>
        public const string LeftSchool = "left the school longer ago than the retention period";

        public async Task<string?> EnsureAsync(SchulwareAccount account, Guid userId)
        {
            if (account.MobileAccessToken is null || account.MobileApiAvailable == false)
                return await EnsureWithoutProfileAsync(account, userId);

            try
            {
                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, account.SchulwareApiBaseUrl,
                    account.SchulnetzBaseUrl, account.MobileAccessToken);

                var info = await client.Api.Mobile.UserInfo.GetAsync();
                if (info is null) return "couldn't fetch your Schulnetz profile";

                // Without this, the sync would recreate the student the host's leaver sweep just deleted.
                if (LeftBeyondRetention(ParseDate(info.ExitDate))) return LeftSchool;

                var schoolId = await schoolData.EnsureSchoolAsync(InstanceUrl.School(account.SchulnetzBaseUrl, account.DisplayName));
                var schoolUserId = await schoolData.EnsureStudentAsync(schoolId, userId, Map(info));

                account.SchoolUserId ??= schoolUserId;
                account.SchulnetzStudentId ??= info.IdNr;
                return null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Auto-provisioning School/SchoolUser failed for {AccountId}", account.Id);
                return "setting up your school profile failed";
            }
        }

        /// <summary>The host's rule for its leaver sweep, read from the host configuration every plugin sees.</summary>
        private bool LeftBeyondRetention(DateOnly? exitDate)
        {
            if (exitDate is null || !configuration.GetValue("Retention:Enabled", true))
                return false;
            return exitDate < DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(-configuration.GetValue("Retention:MonthsAfterLeave", 6));
        }

        private async Task<string?> EnsureWithoutProfileAsync(SchulwareAccount account, Guid userId)
        {
            if (account.SchoolUserId is not null) return null;
            try
            {
                var schoolId = await schoolData.EnsureSchoolAsync(InstanceUrl.School(account.SchulnetzBaseUrl, account.DisplayName));
                account.SchoolUserId = await schoolData.EnsureStudentAsync(schoolId, userId, new StudentRecord());
                return null;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Setting up the school profile without the mobile API failed for {AccountId}", account.Id);
                return "setting up your school profile failed";
            }
        }

        private static StudentRecord Map(UserInfoDto info) => new()
        {
            FirstName = info.FirstName,
            LastName = info.LastName,
            Email = info.Email,
            EntryDate = ParseDate(info.EntryDate),
            LeaveDate = ParseDate(info.ExitDate),
        };

        private static DateOnly? ParseDate(string? raw) =>
            DateOnly.TryParse(raw, out var d) ? d : null;
    }
}
