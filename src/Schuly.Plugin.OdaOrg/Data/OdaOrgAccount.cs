using System.ComponentModel.DataAnnotations.Schema;

namespace Schuly.Plugin.OdaOrg.Data
{
    /// <summary>
    /// A connection to an OdaOrg portal. The login credentials the background sync
    /// replays live in the plugin's in-memory vault, not here — this row holds only
    /// non-secret metadata. Scraped profile/grades/agenda go into the main Schuly DB.
    /// </summary>
    public class OdaOrgAccount
    {
        public Guid Id { get; set; }
        public Guid ApplicationUserId { get; set; }

        /// <summary>Linked main-DB SchoolUser, stamped on first successful sync.</summary>
        public Guid? SchoolUserId { get; set; }

        public required string BaseUrl { get; set; }

        public string? DisplayName { get; set; }

        /// <summary>
        /// When on, the credentials are kept in the in-memory vault and the account
        /// is auto-synced in the background. They are never written to the database,
        /// so after a backend restart the account must be reconnected to re-seed them.
        /// </summary>
        public bool AutoRefresh { get; set; } = true;

        // --- Secrets: held only in the per-plugin vault (encrypted in memory),
        //     never persisted. OdaOrg has no token flow — the scraper replays these
        //     username/password on every sync — so they're hydrated from the vault. ---
        [NotMapped] public string? Username { get; set; }
        [NotMapped] public string? Password { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
