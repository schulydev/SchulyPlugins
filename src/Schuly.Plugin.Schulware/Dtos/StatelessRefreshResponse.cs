using System.Text.Json;

namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessRefreshResponse(bool Success, string? Message, string? AccessToken, string? RefreshToken, string? WebSessionId, string? WebSessionUserId, string? WebSessionTransId, JsonElement? ContextState);
}
