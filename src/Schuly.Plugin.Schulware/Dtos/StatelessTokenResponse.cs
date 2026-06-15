namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>Tokens returned to the caller after an OAuth exchange. Stored nowhere server-side.</summary>
    public record StatelessTokenResponse(string? AccessToken, string? RefreshToken);
}
