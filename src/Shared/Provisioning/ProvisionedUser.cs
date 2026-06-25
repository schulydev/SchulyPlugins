namespace Schuly.Plugin.Shared.Provisioning
{
    /// <summary>Neutral per-user fields each plugin maps from its own profile DTO,
    /// so the SchoolUser provisioning can be shared.</summary>
    public sealed record ProvisionedUser(string? FirstName, string? LastName, string? Email, string? PrivateEmail, string? PhoneNumber, string? Street, string? City, string? Zip, DateOnly? Birthday, DateOnly? EntryDate, DateOnly? LeaveDate, string? ProfilePictureUrl);
}
