using System.Text.Json;

namespace Schuly.Plugin.Schulware.Dtos
{
    /// <summary>
    /// Private-mode passwordless refresh payload. The caller owns persistence, so it
    /// passes its stored <c>context_state</c> (opaque JSON object), school URL and the
    /// exact WebView user agent back in on every refresh.
    /// </summary>
    public record StatelessRefreshRequest(string BaseUrl, string UserAgent, JsonElement ContextState);
}
