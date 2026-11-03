using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Teams chat extras for a linked Microsoft account: chat pictures, pasted images and shared files (with thumbnails) in messages, Teams emojis, the rich composer send, meetings, scheduled messages and GIF search.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}")]
    public class ChatsController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, ScheduledMessageStore scheduled, IdSealer sealer, MicrosoftResponseCache cache) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        /// <summary>A whole send: one 100 MB file plus the text and inline images (the service enforces the same cap).</summary>
        public const long MaxSendBytes = 105L * 1024 * 1024;
        private const int MaxJsonSendBytes = 256 * 1024;
        private const int SmallBodyBytes = 16 * 1024;
        private const int MaxContentChars = 28000;
        private static readonly string[] FormFields = ["content", "text", "contentType", "importance", "mentions", "gif"];
        private static readonly string[] FileFields = ["images", "files"];
        private static readonly string[] ContentTypes = ["text", "html"];
        private static readonly string[] Importances = ["normal", "high", "urgent"];

        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);
        private static readonly Regex MessageIdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex HostedIdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);
        private static readonly Regex UserIdPattern = new("^[A-Za-z0-9@._-]{1,200}$", RegexOptions.Compiled);
        private static readonly Regex AttachmentIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex EmailPattern = new("^[^@\\s<>(),;:\\\\\"\\[\\]']{1,64}@[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?\\.[A-Za-z]{2,}$", RegexOptions.Compiled);
        private static readonly int[] PhotoSizes = [48, 64, 96, 120, 240];

        [HttpGet("chats/{chatId}/photo")]
        public Task<IActionResult> ChatPhoto(Guid accountId, string chatId, CancellationToken ct = default) =>
            ImageAsync(accountId, (ids, c) => Reader.GetChatPhotoAsync(ids.UserId, accountId, Chat(ids, chatId), c), ct);

        /// <summary>An image pasted into a message. <paramref name="hostedId"/> is the message's <c>media[].hostedContentId</c>; it may contain "/", so it is a catch-all.</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/media/{**hostedId}")]
        public Task<IActionResult> ChatMedia(Guid accountId, string chatId, string messageId, string hostedId, CancellationToken ct = default) =>
            ImageAsync(accountId, (ids, c) => Reader.GetChatMediaAsync(ids.UserId, accountId, Chat(ids, chatId), Message(ids, messageId), OpenSafe(ids, IdKinds.HostedContent, PathIds.DecodeCatchAll(hostedId), HostedIdPattern), c), ct);

        [HttpGet("people/photo")]
        public Task<IActionResult> PeoplePhoto(Guid accountId, [FromQuery] string? email, [FromQuery] int size = 48, CancellationToken ct = default)
        {
            email = email?.Trim();
            if (string.IsNullOrEmpty(email) || email.Length > 254 || !EmailPattern.IsMatch(email))
                return Task.FromResult(Invalid("Invalid email."));
            if (!PhotoSizes.Contains(size))
                return Task.FromResult(Invalid("size must be one of 48, 64, 96, 120, 240."));
            return ImageAsync(accountId, (ids, c) => Reader.GetPeoplePhotoAsync(ids.UserId, accountId, email, size, c), ct);
        }

        /// <summary>A file shared in a message (<c>files[].id</c>), streamed through as a download, whole or one range. Video and audio keep their type so a blob can play in &lt;video&gt;.</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/files/{attachmentId}")]
        public Task<IActionResult> ChatFile(Guid accountId, string chatId, string messageId, string attachmentId, CancellationToken ct = default) =>
            RunAsync(accountId, (ids, c) => StreamFileAsync(ids.UserId, accountId, FilePath(ids, chatId, messageId, attachmentId), c), ct);

        /// <summary>The preview image of a file shared in a message (<c>files[].id</c>, only when <c>hasThumbnail</c>): JPEG, PNG or WEBP, 404 when there is none. <paramref name="size"/> is small, medium or large.</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/files/{attachmentId}/thumbnail")]
        public Task<IActionResult> ChatFileThumbnail(Guid accountId, string chatId, string messageId, string attachmentId, [FromQuery] string size = "medium", CancellationToken ct = default)
        {
            if (!EmojiRequests.IsThumbnailSize(size))
                return Task.FromResult(Invalid("size must be small, medium or large."));
            return ImageAsync(accountId, (ids, c) =>
            {
                var path = FilePath(ids, chatId, messageId, attachmentId);
                return Reader.GetImageAsync(ids.UserId, accountId, $"thumb|{path}|{size}", $"{path}/thumbnail?size={size}", c);
            }, ct);
        }

        /// <summary>The preview image of a OneDrive item (an <c>id</c> from the files list): JPEG, PNG or WEBP, 404 when there is none.</summary>
        [HttpGet("files/{itemId}/thumbnail")]
        public Task<IActionResult> DriveThumbnail(Guid accountId, string itemId, [FromQuery] string size = "medium", CancellationToken ct = default)
        {
            if (!EmojiRequests.IsThumbnailSize(size))
                return Task.FromResult(Invalid("size must be small, medium or large."));
            return ImageAsync(accountId, (ids, c) =>
            {
                var item = DriveItemId(ids, itemId);
                return Reader.GetImageAsync(ids.UserId, accountId, $"drivethumb|{item}|{size}", $"files/{Uri.EscapeDataString(item)}/thumbnail?size={size}", c);
            }, ct);
        }

        /// <summary>Teams' emoji catalogue: <c>{locale, version, assetBase, sizes, fps, categories: [{id, title, emojis: [{id, shortcuts, unicode, title, keywords, diverse, animated, staticUrl, animatedUrl, frames, frameMs, firstFrame, etag}]}], custom}</c>. The service caches it for 24 hours; it carries no token, so the browser may keep it too.</summary>
        [HttpGet("emojis/catalogue")]
        public async Task<IActionResult> EmojiCatalogue(Guid accountId, [FromQuery] string locale = "default", CancellationToken ct = default)
        {
            if (!EmojiRequests.IsLocale(locale))
                return Invalid("Invalid locale.");
            var result = await ReadAsync(accountId, $"emojis/catalogue?locale={Uri.EscapeDataString(locale)}", IdShape.None, ct);
            if (result is OkObjectResult)
                Response.Headers.CacheControl = "private, max-age=3600";
            return result;
        }

        /// <summary>A tenant custom emoji's image (<c>emojis[].documentId</c> of a message emoji with kind custom). <paramref name="animated"/> true is a GIF/WEBP that plays in a plain image tag.</summary>
        [HttpGet("emojis/custom/{documentId}")]
        public Task<IActionResult> CustomEmoji(Guid accountId, string documentId, [FromQuery] bool animated = true, CancellationToken ct = default) =>
            ImageAsync(accountId, (ids, c) =>
            {
                var document = ids.Open(IdKinds.Emoji, documentId);
                if (!EmojiRequests.IsDocumentId(document))
                    throw new UnsafeIdException();
                return Reader.GetImageAsync(ids.UserId, accountId, $"emoji|{document}|{animated}", $"emojis/custom/{Uri.EscapeDataString(document)}?animated={animated.ToString().ToLowerInvariant()}", c);
            }, ct);

        /// <summary>Sends a chat message. JSON <c>{content, contentType: "text"|"html", importance, mentions, gif}</c> (legacy <c>{text}</c> still works), or multipart/form-data with the same fields (mentions and gif as JSON strings) plus <c>images</c> (inline, PNG/JPEG/GIF/WEBP, 3 MB total) and <c>files</c> parts (up to 100 MB each).</summary>
        [HttpPost("chats/{chatId}/messages")]
        [RequestSizeLimit(MaxSendBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxSendBytes, ValueLengthLimit = 256 * 1024)]
        public async Task<IActionResult> Send(Guid accountId, string chatId, CancellationToken ct = default)
        {
            if (Request.HasFormContentType)
                return await SendMultipartAsync(accountId, chatId, ct);

            if (Request.ContentLength > MaxJsonSendBytes)
                return TooLarge();
            JsonElement body;
            try
            {
                // Chunked bodies carry no Content-Length, so the cap is enforced while reading: the server limit where it can still be lowered, and a counting stream either way.
                LimitBody(MaxJsonSendBytes);
                await using var limited = new LengthLimitedStream(Request.Body, MaxJsonSendBytes);
                body = await JsonSerializer.DeserializeAsync<JsonElement>(limited, cancellationToken: ct);
            }
            catch (Exception ex) when (IsTooLarge(ex))
            {
                return TooLarge();
            }
            catch (JsonException)
            {
                return Invalid("The body must be a JSON object.");
            }
            if (body.ValueKind != JsonValueKind.Object)
                return Invalid("The body must be a JSON object.");
            if (CheckFields(Str(body, "content") ?? Str(body, "text"), Str(body, "contentType"), Str(body, "importance")) is { } error)
                return Invalid(error);
            return await RunAsync(accountId, async (ids, c) => Sent(ids, await Reader.PostAsync(ids.UserId, accountId, MessagesPath(ids, chatId), OpenMentions(ids, JsonNode.Parse(body.GetRawText())!.AsObject()), c)), ct);
        }

        private OkObjectResult Sent(IdScope ids, JsonElement created)
        {
            cache.AfterChatWrite(ids.UserId, ids.AccountId);
            return Ok(ResponseSealer.Seal(created, IdShape.ChatMessages, ids));
        }

        // ASP.NET spools form files above 64 KB to temporary files (deleted after the request), so nothing large sits in memory; each part is streamed on from there.
        private async Task<IActionResult> SendMultipartAsync(Guid accountId, string chatId, CancellationToken ct)
        {
            if (Request.ContentLength > MaxSendBytes)
                return TooLarge();
            IFormCollection form;
            try
            {
                LimitBody(MaxSendBytes);
                form = await Request.ReadFormAsync(ct);
            }
            catch (Exception ex) when (IsTooLarge(ex))
            {
                return TooLarge();
            }
            if (CheckFields(form["content"].FirstOrDefault() ?? form["text"].FirstOrDefault(), form["contentType"].FirstOrDefault(), form["importance"].FirstOrDefault()) is { } error)
                return Invalid(error);
            if (form.Files.Any(f => !FileFields.Contains(f.Name)))
                return Invalid("File parts must be named images or files.");

            return await RunAsync(accountId, async (ids, c) =>
            {
                using var content = new MultipartFormDataContent();
                foreach (var name in FormFields)
                {
                    if (form[name].FirstOrDefault() is not { } value)
                        continue;
                    if (name == "mentions")
                        value = OpenMentionsField(ids, value);
                    content.Add(new StringContent(value, Encoding.UTF8), name);
                }
                foreach (var file in form.Files)
                {
                    var part = new StreamContent(file.OpenReadStream());
                    part.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
                    part.Headers.ContentLength = file.Length;
                    content.Add(part, file.Name, file.FileName);
                }
                return Sent(ids, await Reader.PostContentAsync(ids.UserId, accountId, MessagesPath(ids, chatId), content, c));
            }, ct);
        }

        /// <summary>Creates a Teams meeting with the chat's members as attendees: <c>{subject, start, end, description?}</c> → <c>{id, subject, start, end, webLink, joinUrl, attendees}</c>.</summary>
        [HttpPost("chats/{chatId}/meeting")]
        [RequestSizeLimit(SmallBodyBytes)]
        public Task<IActionResult> Meeting(Guid accountId, string chatId, [FromBody] MeetingRequest? request, CancellationToken ct = default)
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 255 || request.End <= request.Start || (request.Description?.Length ?? 0) > 4000)
                return Task.FromResult(Invalid("A meeting needs a subject (up to 255 characters) and an end after its start."));
            return RunAsync(accountId, async (ids, c) =>
            {
                var meeting = await Reader.PostAsync(ids.UserId, accountId, $"chats/{Uri.EscapeDataString(Chat(ids, chatId))}/meeting", new { subject = request.Subject.Trim(), start = request.Start, end = request.End, description = request.Description }, c);
                cache.AfterChatWrite(ids.UserId, accountId);
                return StatusCode(StatusCodes.Status201Created, ResponseSealer.Seal(meeting, IdShape.CalendarEvents, ids));
            }, ct);
        }

        /// <summary>Schedules a text/HTML message: <c>{content, contentType, importance?, sendAt}</c>, sendAt within 7 days. Kept encrypted in the plugin vault until sent, then deleted.</summary>
        [HttpPost("chats/{chatId}/scheduled")]
        [RequestSizeLimit(MaxJsonSendBytes)]
        public Task<IActionResult> Schedule(Guid accountId, string chatId, [FromBody] ScheduleMessageRequest? request, CancellationToken ct = default)
        {
            if (request is null)
                return Task.FromResult(Invalid("Missing body."));
            if (CheckFields(request.Content, request.ContentType, request.Importance) is { } error)
                return Task.FromResult(Invalid(error));
            if (string.IsNullOrWhiteSpace(request.Content))
                return Task.FromResult(Invalid("The message is empty."));
            var now = DateTimeOffset.UtcNow;
            if (request.SendAt < now.AddMinutes(1) || request.SendAt > now + ScheduledMessageStore.MaxAhead)
                return Task.FromResult(Invalid("sendAt must be between one minute and seven days from now."));
            return RunAsync(accountId, async (ids, c) =>
            {
                var message = new ScheduledMessage(Guid.NewGuid(), ids.UserId, accountId, Chat(ids, chatId), request.SendAt.ToUniversalTime(), request.Content, request.ContentType ?? "text", request.Importance);
                var saved = await scheduled.AddAsync(message, c);
                return saved is null ? Conflict(new { code = "too_many_scheduled", message = $"At most {ScheduledMessageStore.MaxPerAccount} scheduled messages per account." }) : StatusCode(StatusCodes.Status201Created, View(ids, saved));
            }, ct);
        }

        [HttpGet("scheduled")]
        public Task<IActionResult> Scheduled(Guid accountId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) => Ok((await scheduled.ListAsync(ids.UserId, accountId, c)).Select(m => View(ids, m))), ct);

        /// <summary>Cancels a scheduled message; <paramref name="scheduleId"/> is the sealed <c>id</c> from the list.</summary>
        [HttpDelete("scheduled/{scheduleId}")]
        public Task<IActionResult> CancelScheduled(Guid accountId, string scheduleId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) => Guid.TryParseExact(ids.Open(IdKinds.Scheduled, scheduleId), "N", out var id) && await scheduled.RemoveAsync(ids.UserId, accountId, id, c) ? NoContent() : NotFound(), ct);

        /// <summary>Giphy search (needs the service's GIPHY_API_KEY; 503 without): <c>[{id, title, url, previewUrl, width, height}]</c>. Send a pick as <c>gif: {url, alt, width, height}</c>.</summary>
        [HttpGet("gifs/search")]
        public Task<IActionResult> GifSearch(Guid accountId, [FromQuery] string? q, [FromQuery] int limit = 24, CancellationToken ct = default) =>
            string.IsNullOrWhiteSpace(q) || q.Length > 100 ? Task.FromResult(Invalid("q must be 1-100 characters.")) :
            GifsAsync(accountId, $"gifs/search?q={Uri.EscapeDataString(q.Trim())}&limit={Math.Clamp(limit, 1, 50)}", ct);

        [HttpGet("gifs/trending")]
        public Task<IActionResult> GifTrending(Guid accountId, [FromQuery] int limit = 24, CancellationToken ct = default) =>
            GifsAsync(accountId, $"gifs/trending?limit={Math.Clamp(limit, 1, 50)}", ct);

        private async Task<IActionResult> GifsAsync(Guid accountId, string path, CancellationToken ct)
        {
            try
            {
                return await ReadAsync(accountId, path, IdShape.None, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
            {
                return StatusCode((int)ex.StatusCode!.Value, new { code = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "rate_limited" : "gif_search_unavailable", message = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "Too many GIF searches; wait a minute." : "GIF search is not configured on this server." });
            }
        }

        private IActionResult TooLarge() =>
            StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "invalid_request", message = "The message is larger than the send limit." });

        private void LimitBody(long max)
        {
            if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = max;
        }

        // Kestrel's body limit throws BadHttpRequestException (413), the form reader InvalidDataException, the counting stream its own.
        private static bool IsTooLarge(Exception ex) =>
            ex is LengthLimitedStream.TooLargeException or InvalidDataException || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge);

        private static string? CheckFields(string? content, string? contentType, string? importance)
        {
            if (content is { Length: > MaxContentChars })
                return $"The message may be at most {MaxContentChars} characters.";
            if (contentType is not null && !ContentTypes.Contains(contentType))
                return "contentType must be text or html.";
            if (importance is not null && !Importances.Contains(importance))
                return "importance must be normal, high or urgent.";
            return null;
        }

        private static string? Str(JsonElement body, string name) =>
            body.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static object View(IdScope ids, ScheduledMessage m) => new { id = ids.Seal(IdKinds.Scheduled, m.Id.ToString("N")), chatId = ids.Seal(IdKinds.Chat, m.ChatId), sendAt = m.SendAt, content = m.Content, contentType = m.ContentType, importance = m.Importance };

        private static string Chat(IdScope ids, string chatId) => OpenSafe(ids, IdKinds.Chat, chatId, ChatIdPattern);

        private static string Message(IdScope ids, string messageId) => OpenSafe(ids, IdKinds.ChatMessage, messageId, MessageIdPattern);

        private static string MessagesPath(IdScope ids, string chatId) => $"chats/{Uri.EscapeDataString(Chat(ids, chatId))}/messages";

        /// <summary>The Graph id of a OneDrive item behind a sealed one, checked to be safe as a path segment.</summary>
        internal static string DriveItemId(IdScope ids, string itemId)
        {
            var item = ids.Open(IdKinds.DriveItem, itemId);
            return EmojiRequests.IsDriveItemId(item) ? item : throw new UnsafeIdException();
        }

        /// <summary>The upstream path of a file shared in a chat message, from sealed chat, message and attachment ids.</summary>
        internal static string FilePath(IdScope ids, string chatId, string messageId, string attachmentId) =>
            $"chats/{Uri.EscapeDataString(Chat(ids, chatId))}/messages/{Uri.EscapeDataString(Message(ids, messageId))}/files/{Uri.EscapeDataString(OpenSafe(ids, IdKinds.ChatFile, attachmentId, AttachmentIdPattern))}";

        /// <summary>Opens the sealed user ids of <c>mentions[].mentioned.user.id</c> in a JSON send body.</summary>
        private static JsonObject OpenMentions(IdScope ids, JsonObject body)
        {
            if (body["mentions"] is JsonArray mentions)
                OpenMentionList(ids, mentions);
            return body;
        }

        private static string OpenMentionsField(IdScope ids, string json)
        {
            JsonNode? node;
            try
            {
                node = JsonNode.Parse(json);
            }
            catch (JsonException)
            {
                return json; // the service refuses malformed mentions itself
            }
            if (node is not JsonArray mentions)
                return json;
            OpenMentionList(ids, mentions);
            return mentions.ToJsonString();
        }

        private static void OpenMentionList(IdScope ids, JsonArray mentions)
        {
            foreach (var mention in mentions)
                if (mention?["mentioned"]?["user"] is JsonObject user && user["id"] is JsonValue id && id.TryGetValue<string>(out var sealedId))
                    user["id"] = OpenSafe(ids, IdKinds.User, sealedId, UserIdPattern);
        }
    }

    public record MeetingRequest(string? Subject, DateTimeOffset Start, DateTimeOffset End, string? Description);

    public record ScheduleMessageRequest(string? Content, string? ContentType, string? Importance, DateTimeOffset SendAt);
}
