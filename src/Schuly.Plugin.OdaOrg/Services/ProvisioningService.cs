using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Infrastructure.Storage;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Ensures the main-DB <see cref="School"/> + <see cref="SchoolUser"/> for an
    /// OdaOrg account exist, from the scraped profile. Stamps the account's
    /// SchoolUserId on first run; back-fills blanks afterwards.
    /// </summary>
    public class ProvisioningService(Schuly.Infrastructure.SchulyDbContext mainDb, IDocumentStorage storage, ILogger<ProvisioningService> logger)
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
                    PrivateEmail = profile.PrivateEmail,
                    PhoneNumber = profile.PhoneNumber,
                    Street = profile.Street,
                    City = profile.City,
                    Zip = profile.Zip,
                    Birthday = profile.Birthday ?? DateOnly.FromDateTime(DateTime.UtcNow),
                    EntryDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    Role = Roles.Student,
                    State = UserState.Active,
                    ProfilePictureUrl = await ResolveAvatarAsync(profile.ProfilePictureUrl, null, ct),
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
                if (string.IsNullOrWhiteSpace(existing.PrivateEmail) && profile.PrivateEmail is not null) existing.PrivateEmail = profile.PrivateEmail;
                if (string.IsNullOrWhiteSpace(existing.PhoneNumber) && profile.PhoneNumber is not null) existing.PhoneNumber = profile.PhoneNumber;
                if (string.IsNullOrWhiteSpace(existing.Street) && profile.Street is not null) existing.Street = profile.Street;
                if (string.IsNullOrWhiteSpace(existing.City) && profile.City is not null) existing.City = profile.City;
                if (string.IsNullOrWhiteSpace(existing.Zip) && profile.Zip is not null) existing.Zip = profile.Zip;
                existing.ProfilePictureUrl = await ResolveAvatarAsync(profile.ProfilePictureUrl, existing.ProfilePictureUrl, ct);
                await mainDb.SaveChangesAsync(ct);
            }

            account.SchoolUserId ??= existing.Id;
            return existing.Id;
        }

        /// <summary>
        /// Resolve the value to store in SchoolUser.ProfilePictureUrl:
        /// keep an already-stored blob key / external URL (don't re-upload every
        /// sync → orphan blobs); upload a scraped data: URI to the blobstore and
        /// store its key; pass an http(s) URL through.
        /// </summary>
        private async Task<string?> ResolveAvatarAsync(string? scraped, string? existing, CancellationToken ct)
        {
            if (!string.IsNullOrWhiteSpace(existing) && !existing.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return existing;
            if (string.IsNullOrWhiteSpace(scraped)) return existing;
            if (scraped.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                scraped.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return scraped;
            if (!scraped.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return existing;

            try
            {
                var comma = scraped.IndexOf(',');
                if (comma < 0) return existing;
                var meta = scraped[5..comma];                  // e.g. image/png;base64
                var contentType = meta.Split(';')[0].Trim();   // e.g. image/png, image/webp, image/svg+xml
                var bytes = Convert.FromBase64String(scraped[(comma + 1)..]);
                // Derive the extension from the MIME subtype so any image format
                // works (png, webp, gif, bmp, svg, …), not just png/jpg.
                var subtype = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? contentType[6..].Split('+')[0].Trim().ToLowerInvariant()
                    : "";
                var ext = subtype switch { "jpeg" => "jpg", "" => "img", _ => subtype };
                using var ms = new MemoryStream(bytes);
                var blob = await storage.UploadAsync(ms, $"avatar.{ext}", contentType, ct);
                return blob.Key;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OdaOrg avatar upload failed; leaving previous value");
                return existing;
            }
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
