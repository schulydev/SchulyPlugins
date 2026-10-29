using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>The opaque context of a browser sign-in and the code its callback URL carries.</summary>
    public static class BrowserSignIn
    {
        public sealed record Context(string CodeVerifier, string DeviceId, string State);

        public static string Serialize(Context context) => JsonSerializer.Serialize(context);

        public static Context? Parse(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try { return JsonSerializer.Deserialize<Context>(json); }
            catch (JsonException) { return null; }
        }

        /// <summary>The <c>code</c> of a callback URL whose <c>state</c> matches, or null.</summary>
        public static string? CodeFrom(string? callbackUrl, Context context)
        {
            if (!Uri.TryCreate(callbackUrl, UriKind.Absolute, out var uri)) return null;
            var query = QueryHelpers.ParseQuery(uri.Query);
            if (query["state"] != context.State) return null;
            var code = query["code"].ToString();
            return string.IsNullOrEmpty(code) ? null : code;
        }
    }
}
