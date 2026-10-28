using System.ComponentModel.DataAnnotations.Schema;

namespace Schuly.Plugin.Schulware.Data
{
    public class SchulwareAccount
    {
        public Guid Id { get; set; }
        public Guid ApplicationUserId { get; set; }
        public Guid? SchoolUserId { get; set; }

        public required string SchulnetzBaseUrl { get; set; }
        public required string SchulwareApiBaseUrl { get; set; }
        public string? SchulnetzStudentId { get; set; }
        public string? DisplayName { get; set; }

        public bool AutoRefresh { get; set; } = true;

        public Guid? LinkedAccountId { get; set; }

        /// <summary>Set when the account signed in through a browser; its mobile tokens are bound to this device id.</summary>
        public string? DeviceId { get; set; }

        public DateTime? MobileTokenExpiresAt { get; set; }
        public bool? MobileApiAvailable { get; set; }
        public DateTime? HistorySyncedAt { get; set; }

        [NotMapped] public string? MobileAccessToken { get; set; }
        [NotMapped] public string? MobileRefreshToken { get; set; }
        [NotMapped] public string? WebSessionId { get; set; }
        [NotMapped] public string? WebSessionUserId { get; set; }
        [NotMapped] public string? WebSessionTransId { get; set; }

        [NotMapped] public string? SessionCookiesJson { get; set; }

        [NotMapped] public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
