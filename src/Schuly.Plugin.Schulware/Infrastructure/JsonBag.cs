namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>
    /// Helpers to convert between a JSON object string and the plain
    /// <see cref="Dictionary{TKey,TValue}"/> shape Kiota expects in an
    /// <c>AdditionalData</c> bag.
    /// </summary>
    internal static class JsonBag
    {
        public static Dictionary<string, object> ParseObject(string json)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var dict = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
                dict[prop.Name] = Convert(prop.Value);
            return dict;
        }

        public static object Convert(System.Text.Json.JsonElement el) => el.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Object => el.EnumerateObject()
                .ToDictionary(p => p.Name, p => Convert(p.Value)),
            System.Text.Json.JsonValueKind.Array => el.EnumerateArray().Select(Convert).ToList(),
            System.Text.Json.JsonValueKind.String => el.GetString()!,
            System.Text.Json.JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            System.Text.Json.JsonValueKind.True => true,
            System.Text.Json.JsonValueKind.False => false,
            _ => null!,
        };
    }
}
