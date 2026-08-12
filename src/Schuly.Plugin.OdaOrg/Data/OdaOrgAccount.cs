using System.ComponentModel.DataAnnotations.Schema;

namespace Schuly.Plugin.OdaOrg.Data
{
    public class OdaOrgAccount
    {
        public Guid Id { get; set; }
        public Guid ApplicationUserId { get; set; }

        public Guid? SchoolUserId { get; set; }

        public required string BaseUrl { get; set; }

        public string? DisplayName { get; set; }

        public bool AutoRefresh { get; set; } = true;

        [NotMapped] public string? Username { get; set; }
        [NotMapped] public string? Password { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; }
    }
}
