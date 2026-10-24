using System.Text.Json;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>The Microsoft sign-in session the app captured from the WebView, as stored for a linked account.</summary>
    public static class MicrosoftSession
    {
        public sealed record Cookie(string Domain, string Name, string Path, string Value);

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        /// <summary>Keeps only cookies of Microsoft's sign-in hosts; returns the JSON to store, or null when none are left.</summary>
        public static string? ToStored(IEnumerable<Cookie>? cookies)
        {
            var kept = (cookies ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && c.Value is not null && IsMicrosoftSignInHost(c.Domain))
                .Select(c => new { domain = c.Domain, name = c.Name, path = string.IsNullOrWhiteSpace(c.Path) ? "/" : c.Path, value = c.Value })
                .ToList();
            return kept.Count == 0 ? null : JsonSerializer.Serialize(kept, Json);
        }

        public static bool IsMicrosoftSignInHost(string? domain)
        {
            var host = (domain ?? string.Empty).Trim().TrimStart('.').ToLowerInvariant();
            return host is "login.microsoftonline.com" or "login.microsoft.com" or "login.live.com";
        }
    }
}
