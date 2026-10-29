using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Teams chat extras for a linked Microsoft account: chat pictures, pasted images and shared files in messages, the rich composer send, meetings, scheduled messages and GIF search.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}")]
    public class ChatsController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, ScheduledMessageStore scheduled) : MicrosoftControllerBase(userContext, linkedAccounts, reader)
    {
        /// <summary>A whole send: one 100 MB file plus the text and inline images (the service enforces the same cap).</summary>
        public const long MaxSendBytes = 105L * 1024 * 1024;
        private const int MaxJsonSendBytes = 256 * 1024;
        private const int MaxContentChars = 28000;
        private static readonly string[] FormFields = ["content", "text", "contentType", "importance", "mentions", "gif"];
        private static readonly string[] FileFields = ["images", "files"];
        private static readonly string[] ContentTypes = ["text", "html"];
        private static readonly string[] Importances = ["normal", "high", "urgent"];

        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);
        private static readonly Regex MessageIdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex HostedIdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);
        private static readonly Regex AttachmentIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex EmailPattern = new("^[^@\\s<>(),;:\\\\\"\\[\\]']{1,64}@[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?\\.[A-Za-z]{2,}$", RegexOptions.Compiled);
        private static readonly int[] PhotoSizes = [48, 64, 96, 120, 240];

        [HttpGet("chats/{chatId}/photo")]
        public Task<IActionResult> ChatPhoto(Guid accountId, string chatId, CancellationToken ct = default) =>
            !ChatIdPattern.IsMatch(chatId) ? Task.FromResult(Invalid("Invalid chat id.")) :
            ImageAsync(accountId, (userId, c) => Reader.GetChatPhotoAsync(userId, accountId, chatId, c), ct);

        /// <summary>An image pasted into a message. <paramref name="hostedId"/> is the message's <c>media[].hostedContentId</c>; it may contain "/", so it is a catch-all.</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/media/{**hostedId}")]
        public Task<IActionResult> ChatMedia(Guid accountId, string chatId, string messageId, string hostedId, CancellationToken ct = default)
        {
            hostedId = Uri.UnescapeDataString(hostedId);
            if (!ChatIdPattern.IsMatch(chatId) || !MessageIdPattern.IsMatch(messageId) || !HostedIdPattern.IsMatch(hostedId))
                return Task.FromResult(Invalid("Invalid chat, message or media id."));
            return ImageAsync(accountId, (userId, c) => Reader.GetChatMediaAsync(userId, accountId, chatId, messageId, hostedId, c), ct);
        }

        [HttpGet("people/photo")]
        public Task<IActionResult> PeoplePhoto(Guid accountId, [FromQuery] string? email, [FromQuery] int size = 48, CancellationToken ct = default)
        {
            email = email?.Trim();
            if (string.IsNullOrEmpty(email) || email.Length > 254 || !EmailPattern.IsMatch(email))
                return Task.FromResult(Invalid("Invalid email."));
            if (!PhotoSizes.Contains(size))
                return Task.FromResult(Invalid("size must be one of 48, 64, 96, 120, 240."));
            return ImageAsync(accountId, (userId, c) => Reader.GetPeoplePhotoAsync(userId, accountId, email, size, c), ct);
        }

        /// <summary>A file shared in a message (<c>files[].id</c>), streamed through as a download. Video and audio keep their type so a blob can play in &lt;video&gt;.</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/files/{attachmentId}")]
        public Task<IActionResult> ChatFile(Guid accountId, string chatId, string messageId, string attachmentId, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId) || !MessageIdPattern.IsMatch(messageId) || !AttachmentIdPattern.IsMatch(attachmentId))
                return Task.FromResult(Invalid("Invalid chat, message or file id."));
            return RunAsync(accountId, async (userId, c) =>
            {
                var upstream = await Reader.OpenStreamAsync(userId, accountId, $"chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}/files/{Uri.EscapeDataString(attachmentId)}", c);
                if (upstream is null)
                    return NotFound();
                HttpContext.Response.RegisterForDispose(upstream);
                AttachmentResponse.Apply(Response, upstream.Content.Headers.ContentDisposition?.ToString());
                if (upstream.Content.Headers.ContentLength is { } length)
                    Response.ContentLength = length;
                return File(await upstream.Content.ReadAsStreamAsync(c), AttachmentResponse.SafeContentType(upstream.Content.Headers.ContentType?.MediaType), enableRangeProcessing: false);
            }, ct);
        }

        /// <summary>Sends a chat message. JSON <c>{content, contentType: "text"|"html", importance, mentions, gif}</c> (legacy <c>{text}</c> still works), or multipart/form-data with the same fields (mentions and gif as JSON strings) plus <c>images</c> (inline, PNG/JPEG/GIF/WEBP, 3 MB total) and <c>files</c> parts (up to 100 MB each).</summary>
        [HttpPost("chats/{chatId}/messages")]
        [RequestSizeLimit(MaxSendBytes)]
        [RequestFormLimits(MultipartBodyLengthLimit = MaxSendBytes, ValueLengthLimit = 256 * 1024)]
        public async Task<IActionResult> Send(Guid accountId, string chatId, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId))
                return Invalid("Invalid chat id.");
            var path = $"chats/{Uri.EscapeDataString(chatId)}/messages";
            if (Request.HasFormContentType)
                return await SendMultipartAsync(accountId, path, ct);

            if (Request.ContentLength > MaxJsonSendBytes)
                return StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "invalid_request", message = "The message is too large; send files as multipart." });
            JsonElement body;
            try
            {
                body = await JsonSerializer.DeserializeAsync<JsonElement>(Request.Body, cancellationToken: ct);
            }
            catch (JsonException)
            {
                return Invalid("The body must be a JSON object.");
            }
            if (body.ValueKind != JsonValueKind.Object)
                return Invalid("The body must be a JSON object.");
            if (CheckFields(Str(body, "content") ?? Str(body, "text"), Str(body, "contentType"), Str(body, "importance")) is { } error)
                return Invalid(error);
            return await RunAsync(accountId, async (userId, c) => Ok(await Reader.PostAsync(userId, accountId, path, body, c)), ct);
        }

        // ASP.NET spools form files above 64 KB to temporary files (deleted after the request), so nothing large sits in memory; each part is streamed on from there.
        private async Task<IActionResult> SendMultipartAsync(Guid accountId, string path, CancellationToken ct)
        {
            var form = await Request.ReadFormAsync(ct);
            if (CheckFields(form["content"].FirstOrDefault() ?? form["text"].FirstOrDefault(), form["contentType"].FirstOrDefault(), form["importance"].FirstOrDefault()) is { } error)
                return Invalid(error);
            if (form.Files.Any(f => !FileFields.Contains(f.Name)))
                return Invalid("File parts must be named images or files.");

            using var content = new MultipartFormDataContent();
            foreach (var name in FormFields)
                if (form[name].FirstOrDefault() is { } value)
                    content.Add(new StringContent(value, Encoding.UTF8), name);
            foreach (var file in form.Files)
            {
                var part = new StreamContent(file.OpenReadStream());
                part.Headers.ContentType = MediaTypeHeaderValue.TryParse(file.ContentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
                part.Headers.ContentLength = file.Length;
                content.Add(part, file.Name, file.FileName);
            }
            return await RunAsync(accountId, async (userId, c) => Ok(await Reader.PostContentAsync(userId, accountId, path, content, c)), ct);
        }

        /// <summary>Creates a Teams meeting with the chat's members as attendees: <c>{subject, start, end, description?}</c> → <c>{id, subject, start, end, webLink, joinUrl, attendees}</c>.</summary>
        [HttpPost("chats/{chatId}/meeting")]
        public Task<IActionResult> Meeting(Guid accountId, string chatId, [FromBody] MeetingRequest? request, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId))
                return Task.FromResult(Invalid("Invalid chat id."));
            if (request is null || string.IsNullOrWhiteSpace(request.Subject) || request.Subject.Length > 255 || request.End <= request.Start || (request.Description?.Length ?? 0) > 4000)
                return Task.FromResult(Invalid("A meeting needs a subject (up to 255 characters) and an end after its start."));
            return RunAsync(accountId, async (userId, c) => StatusCode(StatusCodes.Status201Created, await Reader.PostAsync(userId, accountId, $"chats/{Uri.EscapeDataString(chatId)}/meeting", new { subject = request.Subject.Trim(), start = request.Start, end = request.End, description = request.Description }, c)), ct);
        }

        /// <summary>Schedules a text/HTML message: <c>{content, contentType, importance?, sendAt}</c>, sendAt within 7 days. Kept encrypted in the plugin vault until sent, then deleted.</summary>
        [HttpPost("chats/{chatId}/scheduled")]
        public Task<IActionResult> Schedule(Guid accountId, string chatId, [FromBody] ScheduleMessageRequest? request, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId))
                return Task.FromResult(Invalid("Invalid chat id."));
            if (request is null)
                return Task.FromResult(Invalid("Missing body."));
            if (CheckFields(request.Content, request.ContentType, request.Importance) is { } error)
                return Task.FromResult(Invalid(error));
            if (string.IsNullOrWhiteSpace(request.Content))
                return Task.FromResult(Invalid("The message is empty."));
            var now = DateTimeOffset.UtcNow;
            if (request.SendAt < now.AddMinutes(1) || request.SendAt > now + ScheduledMessageStore.MaxAhead)
                return Task.FromResult(Invalid("sendAt must be between one minute and seven days from now."));
            return RunAsync(accountId, async (userId, c) =>
            {
                var message = new ScheduledMessage(Guid.NewGuid(), userId, accountId, chatId, request.SendAt.ToUniversalTime(), request.Content, request.ContentType ?? "text", request.Importance);
                var saved = await scheduled.AddAsync(message, c);
                return saved is null ? Conflict(new { code = "too_many_scheduled", message = $"At most {ScheduledMessageStore.MaxPerAccount} scheduled messages per account." }) : StatusCode(StatusCodes.Status201Created, View(saved));
            }, ct);
        }

        [HttpGet("scheduled")]
        public Task<IActionResult> Scheduled(Guid accountId, CancellationToken ct = default) =>
            RunAsync(accountId, async (userId, c) => Ok((await scheduled.ListAsync(userId, accountId, c)).Select(View)), ct);

        [HttpDelete("scheduled/{scheduleId:guid}")]
        public Task<IActionResult> CancelScheduled(Guid accountId, Guid scheduleId, CancellationToken ct = default) =>
            RunAsync(accountId, async (userId, c) => await scheduled.RemoveAsync(userId, accountId, scheduleId, c) ? NoContent() : NotFound(), ct);

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
                return await ReadAsync(accountId, path, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests)
            {
                return StatusCode((int)ex.StatusCode!.Value, new { code = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "rate_limited" : "gif_search_unavailable", message = ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "Too many GIF searches; wait a minute." : "GIF search is not configured on this server." });
            }
        }

        // Images go on the photo lane and its cache; the browser may keep them an hour (this response carries no token).
        private Task<IActionResult> ImageAsync(Guid accountId, Func<Guid, CancellationToken, Task<PhotoResult>> fetch, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) =>
            {
                var image = await fetch(userId, c);
                if (image.Bytes is null)
                    return NotFound();
                Response.Headers.CacheControl = "private, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(image.Bytes, image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !image.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase) ? image.ContentType : "image/jpeg");
            }, ct);

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

        private static object View(ScheduledMessage m) => new { id = m.Id, chatId = m.ChatId, sendAt = m.SendAt, content = m.Content, contentType = m.ContentType, importance = m.Importance };
    }

    public record MeetingRequest(string? Subject, DateTimeOffset Start, DateTimeOffset End, string? Description);

    public record ScheduleMessageRequest(string? Content, string? ContentType, string? Importance, DateTimeOffset SendAt);
}
