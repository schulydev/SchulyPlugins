namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>Basic profile for private mode, mapped from SchulwareAPI's mobile user info.</summary>
    public record StatelessUserInfoDto(
        string? FirstName,
        string? LastName,
        string? Email,
        string? Birthday,
        string? EntryDate);
}
