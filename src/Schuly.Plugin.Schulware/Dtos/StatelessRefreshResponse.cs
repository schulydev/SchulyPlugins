using System.Text.Json;

namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>
    /// Result of a private-mode refresh. Carries the rotated tokens, web session and
    /// updated <c>context_state</c> back to the caller to persist for the next refresh.
    /// </summary>
    public record StatelessRefreshResponse(bool Success, string? Message, string? AccessToken, string? RefreshToken, string? WebSessionId, string? WebSessionUserId, string? WebSessionTransId, JsonElement? ContextState);
}
