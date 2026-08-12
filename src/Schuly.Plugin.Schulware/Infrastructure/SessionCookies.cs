using System.Text.Json;
using Schuly.Plugin.Schulware.Client.Models;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    internal static class SessionCookies
    {
        public static string? ToJson(List<LoginResponseDto_session_cookies>? cookies)
        {
            if (cookies is not { Count: > 0 }) return null;
            var list = cookies
                .Select(c => c.AdditionalData.ToDictionary(kv => kv.Key, kv => JsonBag.Lower(kv.Value)))
                .ToList();
            return JsonSerializer.Serialize(list);
        }

        public static List<LoginRequestDto_session_cookies>? FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return null;

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return null;

            var result = new List<LoginRequestDto_session_cookies>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var bag = new Dictionary<string, object>();
                if (el.ValueKind == JsonValueKind.Object)
                    foreach (var p in el.EnumerateObject())
                        bag[p.Name] = JsonBag.Convert(p.Value);
                result.Add(new LoginRequestDto_session_cookies { AdditionalData = bag });
            }
            return result.Count > 0 ? result : null;
        }

        public static JsonElement? ToElement(List<LoginResponseDto_session_cookies>? cookies)
        {
            var json = ToJson(cookies);
            return json is null ? null : JsonSerializer.Deserialize<JsonElement>(json);
        }

        public static List<LoginRequestDto_session_cookies>? FromElement(JsonElement el)
            => FromJson(el.GetRawText());
    }
}
