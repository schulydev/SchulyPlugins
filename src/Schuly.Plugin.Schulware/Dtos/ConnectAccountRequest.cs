namespace Schuly.Plugin.Schulware.Dtos
{
    public record ConnectAccountRequest(
        string SchulnetzBaseUrl,
        string? SchulwareApiBaseUrl,
        string? DisplayName,
        Guid? SchoolUserId);
}
