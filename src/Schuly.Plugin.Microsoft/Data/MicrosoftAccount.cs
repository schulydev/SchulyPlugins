namespace Schuly.Plugin.Microsoft.Data
{
    /// <summary>A Microsoft account a Schuly user linked. The refresh token lives in the vault, not here.</summary>
    public class MicrosoftAccount
    {
        public Guid Id { get; set; }
        public Guid ApplicationUserId { get; set; }
        public string? MicrosoftUserId { get; set; }
        public required string Email { get; set; }
        public string? DisplayName { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
