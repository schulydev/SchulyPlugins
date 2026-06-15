namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>Schulnetz authorize URL + PKCE verifier for the app's private-mode OAuth start.</summary>
    public record StatelessAuthorizeUrlResponse(string? AuthorizationUrl, string? CodeVerifier);
}
