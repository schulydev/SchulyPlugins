namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>
    /// Private-mode OAuth exchange payload. Only what SchulwareAPI's callback needs;
    /// the caller keeps its own context_state / user agent locally for refresh.
    /// </summary>
    public record StatelessOAuthCallbackRequest(
        string Code,
        string CodeVerifier,
        string? State,
        string SchulnetzBaseUrl);
}
