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

        public DateTime? MobileTokenExpiresAt { get; set; }

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
