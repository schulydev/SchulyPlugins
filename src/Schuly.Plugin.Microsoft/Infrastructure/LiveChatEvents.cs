using System.Text.Json;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>Turns one line of the Microsoft service's live chat feed into a server-sent event, with the chat id sealed like the chat list's. Whatever isn't known is dropped.</summary>
    public static class LiveChatEvents
    {
        /// <summary><see cref="Sse"/> is what the browser gets, if anything. <see cref="Changed"/> means cached chat answers are out of date.</summary>
        public readonly record struct Relayed(string? Sse, bool Changed);

        private static readonly string[] MessageKinds = ["message", "message_updated", "message_deleted"];

        public static Relayed Relay(string line, IdScope ids)
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("kind", out var kindElement) || kindElement.ValueKind != JsonValueKind.String)
                    return default;
                var kind = kindElement.GetString();
                // A comment: browsers skip it, and proxies see traffic on a quiet stream.
                if (kind == "ping")
                    return new Relayed(": ping\n\n", false);
                // Anything may have been missed before this, so cached answers can't be trusted either.
                if (kind == "connected")
                    return new Relayed(Data(new { kind }), true);
                if (!MessageKinds.Contains(kind) || !root.TryGetProperty("chatId", out var chat) || chat.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(chat.GetString()))
                    return default;
                var fromMe = root.TryGetProperty("fromMe", out var me) && me.ValueKind == JsonValueKind.True;
                return new Relayed(Data(new { kind, chatId = ids.Seal(IdKinds.Chat, chat.GetString()!), fromMe }), true);
            }
            catch (JsonException)
            {
                return default;
            }
        }

        private static string Data(object value) => $"data: {JsonSerializer.Serialize(value)}\n\n";
    }
}
