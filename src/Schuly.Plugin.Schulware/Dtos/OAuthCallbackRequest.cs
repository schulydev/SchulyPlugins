namespace Schuly.Plugin.Schulware.Dtos
{
    public record OAuthCallbackRequest(string Code, string CodeVerifier, string? State);
}
