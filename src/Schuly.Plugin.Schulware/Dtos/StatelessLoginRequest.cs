namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessLoginRequest(string BaseUrl, string Email, string Password, string? TotpSecret);
}
