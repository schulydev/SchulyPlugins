using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Infrastructure.Storage;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Shared.Provisioning;

namespace Schuly.Plugin.OdaOrg.Services
{
    public class ProvisioningService(Schuly.Infrastructure.SchulyDbContext mainDb, IDocumentStorage storage, ILogger<ProvisioningService> logger)
    {
        public async Task<Guid?> EnsureAsync(OdaOrgAccount account, OdaProfile? profile, CancellationToken ct)
        {
            if (profile is null) return account.SchoolUserId;

            var school = await SchoolProvisioner.EnsureSchoolAsync(mainDb, account.BaseUrl, account.DisplayName, ct);

            var existing = await mainDb.SchoolUsers
                .FirstOrDefaultAsync(su => su.ApplicationUserId == account.ApplicationUserId && su.SchoolId == school.Id, ct);
            var avatar = await ResolveAvatarAsync(profile.ProfilePictureUrl, existing?.ProfilePictureUrl, ct);

            var schoolUser = await SchoolProvisioner.EnsureSchoolUserAsync(mainDb, school, account.ApplicationUserId, Map(profile, avatar), ct);

            account.SchoolUserId ??= schoolUser.Id;
            return schoolUser.Id;
        }

        private static ProvisionedUser Map(OdaProfile p, string? avatar) => new(
            p.FirstName, p.LastName, p.Email, p.PrivateEmail, p.PhoneNumber,
            p.Street, p.City, p.Zip, p.Birthday, null, null, avatar);

        /// <summary>
        /// Resolve the value to store in SchoolUser.ProfilePictureUrl: keep an
        /// already-stored blob key / external URL (don't re-upload every sync ->
        /// orphan blobs); upload a scraped data: URI to the blobstore and store its
        /// key; pass an http(s) URL through.
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
                var meta = scraped[5..comma];
                var contentType = meta.Split(';')[0].Trim();
                var bytes = Convert.FromBase64String(scraped[(comma + 1)..]);
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
    }
}
