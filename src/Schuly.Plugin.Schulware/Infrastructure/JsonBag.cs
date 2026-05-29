using Microsoft.Kiota.Abstractions.Serialization;

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

        /// <summary>
        /// Serialize a Kiota <c>AdditionalData</c> bag back to a JSON object string.
        /// Values deserialized by Kiota are <see cref="UntypedNode"/> instances
        /// (UntypedArray/UntypedObject/…); <see cref="System.Text.Json"/> can't
        /// serialize those directly — it writes arrays/objects as empty <c>{}</c>,
        /// silently corrupting the data. We first lower the untyped tree to plain
        /// CLR objects, then serialize.
        /// </summary>
        public static string Serialize(IDictionary<string, object> bag) =>
            System.Text.Json.JsonSerializer.Serialize(
                bag.ToDictionary(kv => kv.Key, kv => Plainify(kv.Value)));

        private static object? Plainify(object? node) => node switch
        {
            UntypedObject o => o.GetValue().ToDictionary(kv => kv.Key, kv => Plainify(kv.Value)),
            UntypedArray a => a.GetValue().Select(Plainify).ToList(),
            UntypedString s => s.GetValue(),
            UntypedBoolean b => b.GetValue(),
            UntypedInteger i => i.GetValue(),
            UntypedLong l => l.GetValue(),
            UntypedDouble d => d.GetValue(),
            UntypedDecimal m => m.GetValue(),
            UntypedFloat f => f.GetValue(),
            UntypedNull => null,
            // Already-plain values (e.g. from ParseObject): recurse into nested
            // collections so mixed trees normalize too.
            IDictionary<string, object?> dict => dict.ToDictionary(kv => kv.Key, kv => Plainify(kv.Value)),
            string str => str,
            System.Collections.IEnumerable seq => seq.Cast<object?>().Select(Plainify).ToList(),
            _ => node,
        };

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
