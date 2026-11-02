using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>What a SchulyMicrosoft answer holds at its top level: decides the kind of the top-level items' <c>id</c>.</summary>
    public enum IdShape
    {
        /// <summary>Passed through untouched (public data without Graph ids: emoji catalogue, GIF search).</summary>
        None,
        Me,
        MailMessages,
        MailFolders,
        MailAttachments,
        CalendarEvents,
        DriveItems,
        Chats,
        ChatMessages,
        Notebooks,
        OneNoteSections,
        OneNotePages,
        OneNotePage,
        Teams,
        Channels,
        /// <summary><c>{driveId, folder, items[]}</c> of a team or channel library folder.</summary>
        TeamFiles,
        /// <summary>Drive items addressed with their drive id (<c>drives/*</c>, shared, recent): item and parent ids seal as <see cref="IdKinds.DriveItemOther"/>.</summary>
        DriveItemsInDrive,
        ChannelMessages,
        /// <summary>The SearchResult: each hit's id is sealed as the kind of its <c>type</c> (file, mail, chat, channel).</summary>
        Search,
        /// <summary>A file text answer: no ids expected, any that show up are sealed opaque.</summary>
        FileText,
    }

    /// <summary>
    /// Seals every id in a SchulyMicrosoft JSON answer before it leaves the plugin, nested ones included. Ids a route takes get their own
    /// kind (chat, message, folder, file, user...); every other <c>id</c> / <c>*Id</c> / <c>*Ids</c> value is sealed as
    /// <see cref="IdKinds.Opaque"/>, so no raw Graph id reaches a client while equal ids stay equal. Graph's <c>@odata.*</c> annotations
    /// (context, nextLink, etag: they embed ids) are dropped, except <c>@odata.type</c>. Links meant to be opened (webUrl, webLink, joinUrl)
    /// are kept as they are: they point at Microsoft's own apps and are not accepted back by any route.
    /// </summary>
    public static partial class ResponseSealer
    {
        private static readonly Dictionary<string, string> NamedKinds = new(StringComparer.Ordinal)
        {
            ["userId"] = IdKinds.User,
            ["chatId"] = IdKinds.Chat,
            ["replyToId"] = IdKinds.ChatMessage,
            ["parentFolderId"] = IdKinds.MailFolder,
            ["conversationId"] = IdKinds.MailConversation,
            ["hostedContentId"] = IdKinds.HostedContent,
            ["documentId"] = IdKinds.Emoji,
            ["notebookId"] = IdKinds.Notebook,
            ["sectionId"] = IdKinds.Section,
            ["driveId"] = IdKinds.Drive,
            ["teamId"] = IdKinds.Team,
            ["channelId"] = IdKinds.Channel,
        };

        // *Id names that are not Graph object ids: a mail attachment's MIME Content-ID, which the mail body refers to as cid:.
        private static readonly HashSet<string> RawNames = new(StringComparer.Ordinal) { "contentId" };

        // The kind of an object's "id", by the property the object sits under.
        private static readonly Dictionary<string, string> ContainerKinds = new(StringComparer.Ordinal)
        {
            ["user"] = IdKinds.User,
            ["childFolders"] = IdKinds.MailFolder,
            ["parentReference"] = IdKinds.DriveItem,
            ["folder"] = IdKinds.DriveItem,
            ["files"] = IdKinds.ChatFile,
            ["resources"] = IdKinds.Resource,
            ["notebook"] = IdKinds.Notebook,
            ["parentNotebook"] = IdKinds.Notebook,
            ["section"] = IdKinds.Section,
            ["parentSection"] = IdKinds.Section,
            ["parentSectionGroup"] = IdKinds.SectionGroup,
            ["lastMessagePreview"] = IdKinds.ChatMessage,
        };

        // Objects whose "id" is not a Graph object id and stays as it is: Teams emoji catalogue ids and OneNote page-element ids (content anchors the page edit sends back).
        private static readonly HashSet<string> RawIdContainers = new(StringComparer.Ordinal) { "emojis", "outlines" };

        /// <summary>Graph's self chat ("Notes (you)") has this fixed id; it is flagged as <c>isSelfChat</c> before the id is sealed.</summary>
        public const string SelfChatId = "48:notes";

        [GeneratedRegex(@"https://graph\.microsoft\.com/[^\s""'<>]*", RegexOptions.IgnoreCase)]
        private static partial Regex GraphUrl();

        [GeneratedRegex(@"data-resource=""([^""]*)""")]
        private static partial Regex DataResource();

        [GeneratedRegex(@"^/drives/[^/]+/")]
        private static partial Regex DrivePathPrefix();

        public static JsonNode? Seal(JsonElement data, IdShape shape, IdScope scope)
        {
            var node = data.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(data.GetRawText());
            Seal(node, shape, scope);
            return node;
        }

        public static void Seal(JsonNode? root, IdShape shape, IdScope scope)
        {
            if (shape == IdShape.None || root is null)
                return;
            var itemKind = ItemKind(shape);
            switch (root)
            {
                case JsonArray items:
                    foreach (var item in items)
                        Walk(item, scope, shape, null, itemKind);
                    break;
                case JsonObject obj when shape is IdShape.Search && obj["hits"] is JsonArray hits:
                    foreach (var hit in hits)
                        if (hit is JsonObject hitObject)
                            WalkObject(hitObject, scope, shape, "hits", HitKind(Text(hitObject["type"])));
                    WalkObject(obj, scope, shape, null, null, skip: "hits");
                    break;
                case JsonObject obj when shape is IdShape.OneNotePages or IdShape.TeamFiles && obj["items"] is JsonArray pages:
                    foreach (var item in pages)
                        Walk(item, scope, shape, null, itemKind);
                    WalkObject(obj, scope, shape, null, null, skip: "items");
                    break;
                default:
                    Walk(root, scope, shape, null, itemKind);
                    break;
            }
        }

        private static string? ItemKind(IdShape shape) => shape switch
        {
            IdShape.Me => IdKinds.User,
            IdShape.MailMessages => IdKinds.MailMessage,
            IdShape.MailFolders => IdKinds.MailFolder,
            IdShape.MailAttachments => IdKinds.MailAttachment,
            IdShape.CalendarEvents => IdKinds.CalendarEvent,
            IdShape.DriveItems => IdKinds.DriveItem,
            IdShape.Chats => IdKinds.Chat,
            IdShape.ChatMessages => IdKinds.ChatMessage,
            IdShape.Notebooks => IdKinds.Notebook,
            IdShape.Teams => IdKinds.Team,
            IdShape.Channels => IdKinds.Channel,
            IdShape.TeamFiles or IdShape.DriveItemsInDrive => IdKinds.DriveItemOther,
            IdShape.ChannelMessages => IdKinds.ChannelMessage,
            IdShape.OneNotePages or IdShape.OneNotePage => IdKinds.Page,
            _ => null,
        };

        // A search hit's id opens as the kind the matching route takes: file -> drive item, mail -> mail message, chat -> chat message, channel -> channel message.
        private static string HitKind(string? type) => type switch
        {
            "file" => IdKinds.DriveItemOther,
            "mail" => IdKinds.MailMessage,
            "chat" => IdKinds.ChatMessage,
            "channel" => IdKinds.ChannelMessage,
            _ => IdKinds.Opaque,
        };

        private static void Walk(JsonNode? node, IdScope scope, IdShape shape, string? container, string? itemKind)
        {
            switch (node)
            {
                case JsonObject obj:
                    WalkObject(obj, scope, shape, container, itemKind);
                    break;
                case JsonArray array:
                    foreach (var child in array)
                        Walk(child, scope, shape, container, null);
                    break;
            }
        }

        private static void WalkObject(JsonObject obj, IdScope scope, IdShape shape, string? container, string? itemKind, string? skip = null)
        {
            var idKind = IdKindOf(obj, shape, container, itemKind);
            if (shape == IdShape.Chats && itemKind is not null)
                obj["isSelfChat"] = Text(obj["id"]) == SelfChatId;

            foreach (var name in obj.Select(p => p.Key).ToList())
            {
                if (name == skip)
                    continue;
                var value = obj[name];
                if (name.StartsWith("@odata.", StringComparison.Ordinal))
                {
                    if (name != "@odata.type")
                        obj.Remove(name);
                    continue;
                }
                if (name == "id")
                {
                    if (Text(value) is { Length: > 0 } raw && idKind is not null)
                        obj[name] = scope.Seal(idKind, raw);
                    continue;
                }
                if (Text(value) is { Length: > 0 } text && (NamedKind(name, shape, out var kind) || (IsIdName(name) && !RawNames.Contains(name))))
                {
                    obj[name] = scope.Seal(kind ?? IdKinds.Opaque, text);
                    continue;
                }
                if (value is JsonArray list && name.EndsWith("Ids", StringComparison.Ordinal))
                {
                    for (var i = 0; i < list.Count; i++)
                        if (Text(list[i]) is { Length: > 0 } each)
                            list[i] = scope.Seal(IdKinds.Opaque, each);
                    continue;
                }
                if (name == "body" && value is JsonObject body && Text(body["content"]) is { } content)
                    body["content"] = GraphUrl().Replace(content, "");
                else if (name == "html" && shape == IdShape.OneNotePage && Text(value) is { } html)
                    obj[name] = DataResource().Replace(html, m => $"data-resource=\"{scope.Seal(IdKinds.Resource, System.Net.WebUtility.HtmlDecode(m.Groups[1].Value))}\"");
                else if (name == "path" && container is "parentReference" or "folder" && Text(value) is { } path)
                    obj[name] = DrivePathPrefix().Replace(path, "/drive/");
                Walk(value, scope, shape, name, null);
            }
        }

        private static bool NamedKind(string name, IdShape shape, out string? kind)
        {
            // A channel reply points at a channel message, not a chat message.
            if (name == "replyToId" && shape == IdShape.ChannelMessages)
            {
                kind = IdKinds.ChannelMessage;
                return true;
            }
            return NamedKinds.TryGetValue(name, out kind);
        }

        private static string? IdKindOf(JsonObject obj, IdShape shape, string? container, string? itemKind)
        {
            if (container is not null && RawIdContainers.Contains(container))
                return container == "emojis" && Text(obj["kind"]) == "custom" ? IdKinds.Opaque : null;
            if (container == "user" || obj.ContainsKey("userIdentityType"))
                return IdKinds.User;
            if (container == "children" && shape == IdShape.OneNoteSections)
                return Text(obj["kind"]) == "group" ? IdKinds.SectionGroup : IdKinds.Section;
            if (container == "attachments")
                return shape == IdShape.ChatMessages ? IdKinds.ChatFile : shape == IdShape.ChannelMessages ? IdKinds.Opaque : IdKinds.MailAttachment;
            if (container is "parentReference" or "folder" && shape is IdShape.TeamFiles or IdShape.DriveItemsInDrive or IdShape.Search)
                return IdKinds.DriveItemOther;
            if (container is not null && ContainerKinds.TryGetValue(container, out var kind))
                return kind;
            return itemKind ?? IdKinds.Opaque;
        }

        // tenantId, iCalUId, internetMessageId, seriesMasterId, teamsAppId, driveId, callId...
        private static bool IsIdName(string name) => name.Length > 2 && name.EndsWith("Id", StringComparison.Ordinal);

        private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    }
}
