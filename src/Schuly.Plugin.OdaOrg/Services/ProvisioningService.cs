using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Ensures the main-DB <see cref="School"/> + <see cref="SchoolUser"/> for an
    /// OdaOrg account exist, from the scraped profile. Stamps the account's
    /// SchoolUserId on first run; back-fills blanks afterwards.
    /// </summary>
    public class ProvisioningService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<ProvisioningService> logger)
    {
        public async Task<Guid?> EnsureAsync(OdaOrgAccount account, OdaProfile? profile, CancellationToken ct)
        {
            if (profile is null) return account.SchoolUserId;

            var school = await GetOrCreateSchoolAsync(account, ct);

            var existing = await mainDb.SchoolUsers
                .FirstOrDefaultAsync(su => su.ApplicationUserId == account.ApplicationUserId && su.SchoolId == school.Id, ct);

            if (existing is null)
            {
                existing = new SchoolUser
                {
                    ApplicationUserId = account.ApplicationUserId,
                    SchoolId = school.Id,
                    FirstName = profile.FirstName ?? "",
                    LastName = profile.LastName ?? "",
                    Email = profile.Email ?? "",
                    Birthday = profile.Birthday ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    EntryDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Role = Roles.Student,
                    State = UserState.Active,
                    ProfilePictureUrl = profile.ProfilePictureUrl,
                };
                mainDb.SchoolUsers.Add(existing);
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Provisioned SchoolUser {Id} for OdaOrg account {Account}", existing.Id, account.Id);
            }
            else
            {
                // Back-fill blanks only — never clobber manual edits.
                if (string.IsNullOrWhiteSpace(existing.FirstName)) existing.FirstName = profile.FirstName ?? existing.FirstName;
                if (string.IsNullOrWhiteSpace(existing.LastName)) existing.LastName = profile.LastName ?? existing.LastName;
                if (string.IsNullOrWhiteSpace(existing.Email) && profile.Email is not null) existing.Email = profile.Email;
                // Photo is authoritative from the source — refresh whenever scraped.
                if (!string.IsNullOrWhiteSpace(profile.ProfilePictureUrl)) existing.ProfilePictureUrl = profile.ProfilePictureUrl;
                await mainDb.SaveChangesAsync(ct);
            }

            account.SchoolUserId ??= existing.Id;
            return existing.Id;
        }

        private async Task<School> GetOrCreateSchoolAsync(OdaOrgAccount account, CancellationToken ct)
        {
            var name = account.DisplayName is { Length: > 0 } d ? d : "OdaOrg";
            var logo = LogoUrlFor(account.BaseUrl);
            var school = await mainDb.Schools.FirstOrDefaultAsync(s => s.Name == name, ct);
            if (school is null)
            {
                school = new School { Name = name, Website = account.BaseUrl, LogoUrl = logo };
                mainDb.Schools.Add(school);
                await mainDb.SaveChangesAsync(ct);
            }
            else
            {
                // Backfill blanks only — don't clobber an admin-set website/logo.
                if (string.IsNullOrWhiteSpace(school.Website)) school.Website = account.BaseUrl;
                if (string.IsNullOrWhiteSpace(school.LogoUrl)) school.LogoUrl = logo;
                await mainDb.SaveChangesAsync(ct);
            }
            return school;
        }

        /// <summary>School logo via a public favicon resolver keyed by host —
        /// no auth, real per-school icon. Admins can override on the School row.</summary>
        private static string? LogoUrlFor(string baseUrl) =>
            Uri.TryCreate(baseUrl, UriKind.Absolute, out var u)
                ? $"https://icons.duckduckgo.com/ip3/{u.Host}.ico"
                : null;
    }
}
