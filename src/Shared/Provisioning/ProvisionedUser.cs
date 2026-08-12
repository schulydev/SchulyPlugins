namespace Schuly.Plugin.Shared.Provisioning
{
    public sealed record ProvisionedUser(string? FirstName, string? LastName, string? Email, string? PrivateEmail, string? PhoneNumber, string? Street, string? City, string? Zip, DateOnly? Birthday, DateOnly? EntryDate, DateOnly? LeaveDate, string? ProfilePictureUrl);
}
