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

        public string? MobileAccessToken { get; set; }
        public string? MobileRefreshToken { get; set; }
        public DateTime? MobileTokenExpiresAt { get; set; }

        public string? WebSessionId { get; set; }
        public string? WebSessionUserId { get; set; }
        public string? WebSessionTransId { get; set; }

        /// <summary>Playwright storage_state blob captured during the user's interactive
        /// OAuth login (cookies + localStorage). Sent to SchulwareAPI's /api/authenticate/refresh
        /// for passwordless re-auth. Updated value is persisted back after each refresh.</summary>
        public string? ContextStateJson { get; set; }

        /// <summary>User-Agent string the cookies were captured with. Microsoft binds
        /// session cookies to UA — must be replayed identically on refresh.</summary>
        public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
