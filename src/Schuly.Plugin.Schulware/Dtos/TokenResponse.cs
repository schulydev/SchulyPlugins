namespace Schuly.Plugin.Schulware.Dtos
{
    public record TokenResponse([property: System.Text.Json.Serialization.JsonPropertyName("access_token")] string? AccessToken, [property: System.Text.Json.Serialization.JsonPropertyName("refresh_token")] string? RefreshToken);
}
