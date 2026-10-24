using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Shared.Provisioning;

namespace Schuly.Plugin.OdaOrg.Services
{
    public class ProvisioningService(ISchoolData schoolData, IPluginFileStore files, IPluginVault vault, ILogger<ProvisioningService> logger)
    {
        public async Task<Guid?> EnsureAsync(OdaOrgAccount account, OdaProfile? profile, CancellationToken ct)
        {
            if (profile is null) return account.SchoolUserId;

            var schoolId = await schoolData.EnsureSchoolAsync(InstanceUrl.School(account.BaseUrl, account.DisplayName), ct);
            var avatar = await ResolveAvatarAsync(account.Id, profile.ProfilePictureUrl, ct);
            var schoolUserId = await schoolData.EnsureStudentAsync(schoolId, account.ApplicationUserId, Map(profile, avatar), ct);

            account.SchoolUserId ??= schoolUserId;
            return schoolUserId;
        }

        private static StudentRecord Map(OdaProfile p, string? avatar) => new()
        {
            FirstName = p.FirstName,
            LastName = p.LastName,
            Email = p.Email,
            PrivateEmail = p.PrivateEmail,
            PhoneNumber = p.PhoneNumber,
            Street = p.Street,
            City = p.City,
            Zip = p.Zip,
            Birthday = p.Birthday,
            ProfilePictureUrl = avatar,
        };

        /// <summary>
        /// The value for the student's profile picture: an http(s) URL passes through; a
        /// scraped data: URI is uploaded once and its storage key reused while the image is
        /// unchanged (the last upload's hash and key sit in the vault). Null leaves it as is.
        /// </summary>
        private async Task<string?> ResolveAvatarAsync(Guid accountId, string? scraped, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(scraped)) return null;
            if (scraped.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                scraped.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return scraped;
            if (!scraped.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return null;

            var vaultKey = $"avatar:{accountId}";
            var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(scraped)));
            if (vault.TryGet(vaultKey, out var stored) && stored.StartsWith(hash + "|", StringComparison.Ordinal))
                return stored[(hash.Length + 1)..];

            try
            {
                var comma = scraped.IndexOf(',');
                if (comma < 0) return null;
                var meta = scraped[5..comma];
                var contentType = meta.Split(';')[0].Trim();
                var bytes = Convert.FromBase64String(scraped[(comma + 1)..]);
                var subtype = contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    ? contentType[6..].Split('+')[0].Trim().ToLowerInvariant()
                    : "";
                var ext = subtype switch { "jpeg" => "jpg", "" => "img", _ => subtype };
                using var ms = new MemoryStream(bytes);
                var file = await files.SaveAsync(ms, $"avatar.{ext}", contentType, ct);
                vault.Set(vaultKey, $"{hash}|{file.Key}");
                return file.Key;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "OdaOrg avatar upload failed; leaving previous value");
                return null;
            }
        }
    }
}
