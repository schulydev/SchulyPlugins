namespace Schuly.Plugin.OdaOrg.Data
{
    /// <summary>
    /// A connection to an OdaOrg portal. This is the credential facade: it
    /// stores the login the background sync replays. No domain data lives here —
    /// scraped profile/grades/agenda are written into the main Schuly DB.
    /// </summary>
    public class OdaOrgAccount
    {
        public Guid Id { get; set; }
        public Guid ApplicationUserId { get; set; }

        /// <summary>Linked main-DB SchoolUser, stamped on first successful sync.</summary>
        public Guid? SchoolUserId { get; set; }

        public required string BaseUrl { get; set; }
        public required string Username { get; set; }

        /// <summary>OdaOrg has no OAuth/token flow — login is a plain form POST,
        /// so the password must be replayed on every sync. Stored as given,
        /// matching the existing plugins' credential-storage posture.</summary>
        public required string Password { get; set; }

        public string? DisplayName { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
