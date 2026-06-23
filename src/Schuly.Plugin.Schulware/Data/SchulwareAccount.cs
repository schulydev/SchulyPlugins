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

        /// <summary>
        /// When on, the auth secrets are kept in the in-memory plugin vault and the
        /// account is auto-refreshed/synced in the background. The secrets are never
        /// written to the database, so after a backend restart the vault is empty and
        /// the account must be reconnected to re-seed it.
        /// </summary>
        public bool AutoRefresh { get; set; } = true;

        /// <summary>Not a secret — kept in the DB so the scheduler knows when to refresh.</summary>
        public DateTime? MobileTokenExpiresAt { get; set; }

        // --- Secrets: held only in the per-plugin vault (encrypted in memory),
        //     never persisted to the database. Hydrated onto the entity at use
        //     time by AccountSecretStore. ---
        [NotMapped] public string? MobileAccessToken { get; set; }
        [NotMapped] public string? MobileRefreshToken { get; set; }
        [NotMapped] public string? WebSessionId { get; set; }
        [NotMapped] public string? WebSessionUserId { get; set; }
        [NotMapped] public string? WebSessionTransId { get; set; }

        /// <summary>Microsoft session_cookies (JSON array) for passwordless re-auth via SchulwareAPI.</summary>
        [NotMapped] public string? SessionCookiesJson { get; set; }

        /// <summary>User-Agent the session cookies were captured with (must be replayed on refresh).</summary>
        [NotMapped] public string? UserAgent { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
