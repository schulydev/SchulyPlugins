using System.Text.Json;

namespace Schuly.Plugin.Schulware.Dtos
{
    public record StatelessRefreshRequest(string BaseUrl, string UserAgent, JsonElement ContextState);
}
