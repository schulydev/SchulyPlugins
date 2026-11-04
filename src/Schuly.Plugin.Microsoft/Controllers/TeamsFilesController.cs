using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Read-only Teams and shared files, search, channel messages and file text for a linked Microsoft account. Thin proxies to SchulyMicrosoft: every id arrives sealed, is opened and checked against its raw pattern here and re-escaped per segment upstream, and every id in the JSON answer is sealed. Search queries only travel in POST bodies and are never logged.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}")]
    public class TeamsFilesController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private static string Team(IdScope ids, string teamId) => Opened(ids, IdKinds.Team, teamId, TeamsFileRequests.IsTeamId);

        private static string Channel(IdScope ids, string channelId) => Opened(ids, IdKinds.Channel, channelId, TeamsFileRequests.IsChannelId);

        private static string Drive(IdScope ids, string driveId) => Opened(ids, IdKinds.Drive, driveId, TeamsFileRequests.IsDriveId);

        private static string Item(IdScope ids, string itemId) => Opened(ids, IdKinds.DriveItemOther, itemId, TeamsFileRequests.IsItemId);

        private static string ChannelMessage(IdScope ids, string messageId) => Opened(ids, IdKinds.ChannelMessage, messageId, TeamsFileRequests.IsChannelMessageId);

        /// <summary>The raw id behind a sealed one (404 when it does not open as <paramref name="kind"/>), checked against the contract pattern: the second layer.</summary>
        private static string Opened(IdScope ids, string kind, string? sealedId, Func<string?, bool> valid)
        {
            var raw = ids.Open(kind, sealedId);
            return valid(raw) ? raw : throw new UnsafeIdException();
        }

        private static string E(string value) => Uri.EscapeDataString(value);

        internal static string DriveItemPath(IdScope ids, string driveId, string itemId) => $"drives/{E(Drive(ids, driveId))}/items/{E(Item(ids, itemId))}";

        private static string ChannelMessages(IdScope ids, string teamId, string channelId) => $"teams/{E(Team(ids, teamId))}/channels/{E(Channel(ids, channelId))}/messages";

        private static int Top(int? top, int fallback) => TeamsFileRequests.Clamp(top, fallback, 1, TeamsFileRequests.MaxTop);

        /// <summary>The joined teams: <c>[{id, displayName, description}]</c>.</summary>
        [HttpGet("teams")]
        public Task<IActionResult> Teams(Guid accountId, CancellationToken ct = default) => ReadAsync(accountId, "teams", IdShape.Teams, ct);

        /// <summary>A team's channels: <c>[{id, displayName, membershipType, description}]</c>.</summary>
        [HttpGet("teams/{teamId}/channels")]
        public Task<IActionResult> Channels(Guid accountId, string teamId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"teams/{E(Team(ids, teamId))}/channels", IdShape.Channels, ct);

        /// <summary>A team's library root, or a channel's files folder, optionally a sub-folder <paramref name="path"/> of it: <c>{driveId, folder, items}</c>.</summary>
        [HttpGet("teams/{teamId}/files")]
        public Task<IActionResult> TeamFiles(Guid accountId, string teamId, [FromQuery] string? channelId = null, [FromQuery] string? path = null, [FromQuery] int? top = null, CancellationToken ct = default)
        {
            if (!TeamsFileRequests.IsRelativePath(path))
                return Task.FromResult(Invalid("Invalid path."));
            return ReadAsync(accountId, ids =>
            {
                var query = $"top={Top(top, TeamsFileRequests.DefaultListTop)}";
                if (channelId is not null)
                    query += $"&channel_id={E(Channel(ids, channelId))}";
                if (path is not null)
                    query += $"&path={E(path)}";
                return $"teams/{E(Team(ids, teamId))}/files?{query}";
            }, IdShape.TeamFiles, ct);
        }

        /// <summary>One drive item with a fresh download URL.</summary>
        [HttpGet("drives/{driveId}/items/{itemId}")]
        public Task<IActionResult> Item(Guid accountId, string driveId, string itemId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => DriveItemPath(ids, driveId, itemId), IdShape.DriveItemsInDrive, ct);

        /// <summary>The children of a drive folder: <c>[driveItem]</c>.</summary>
        [HttpGet("drives/{driveId}/items/{itemId}/children")]
        public Task<IActionResult> Children(Guid accountId, string driveId, string itemId, [FromQuery] int? top = null, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"{DriveItemPath(ids, driveId, itemId)}/children?top={Top(top, TeamsFileRequests.DefaultListTop)}", IdShape.DriveItemsInDrive, ct);

        /// <summary>A drive file's bytes, streamed through as a download, whole or one range (100 MB cap).</summary>
        [HttpGet("drives/{driveId}/items/{itemId}/content")]
        public Task<IActionResult> Content(Guid accountId, string driveId, string itemId, CancellationToken ct = default) =>
            RunAsync(accountId, (ids, c) => StreamFileAsync(ids.UserId, accountId, $"{DriveItemPath(ids, driveId, itemId)}/content", c), ct);

        /// <summary>A drive Office file as PDF, like <c>files/{itemId}/pdf</c>.</summary>
        [HttpGet("drives/{driveId}/items/{itemId}/pdf")]
        public Task<IActionResult> Pdf(Guid accountId, string driveId, string itemId, CancellationToken ct = default) =>
            PdfAsync(accountId, (ids, c) => Reader.OpenStreamAsync(ids.UserId, accountId, $"{DriveItemPath(ids, driveId, itemId)}/pdf", c), ct);

        /// <summary>The preview image of a drive item: JPEG, PNG or WEBP, 404 when there is none.</summary>
        [HttpGet("drives/{driveId}/items/{itemId}/thumbnail")]
        public Task<IActionResult> Thumbnail(Guid accountId, string driveId, string itemId, [FromQuery] string size = "medium", CancellationToken ct = default)
        {
            if (!EmojiRequests.IsThumbnailSize(size))
                return Task.FromResult(Invalid("size must be small, medium or large."));
            return ImageAsync(accountId, (ids, c) =>
            {
                var path = DriveItemPath(ids, driveId, itemId);
                return Reader.GetImageAsync(ids.UserId, accountId, $"driveitemthumb|{path}|{size}", $"{path}/thumbnail?size={size}", c);
            }, ct, optional: true);
        }

        /// <summary>The text of a drive file (docx, xlsx, pptx, pdf, plain text...), paged: <c>{name, kind, text, offset, nextOffset, totalChars, truncated, notes, ...}</c>. 415 <c>unsupported_type</c> and 413 <c>too_large</c> like the PDF routes.</summary>
        [HttpGet("drives/{driveId}/items/{itemId}/text")]
        public Task<IActionResult> DriveText(Guid accountId, string driveId, string itemId, [FromQuery] int? offset = null, [FromQuery] int? maxChars = null, [FromQuery] string? sheet = null, [FromQuery] int? rowOffset = null, [FromQuery] int? maxRows = null, CancellationToken ct = default) =>
            TextRead(accountId, ids => $"{DriveItemPath(ids, driveId, itemId)}/text", offset, maxChars, sheet, rowOffset, maxRows, ct);

        /// <summary>The text of an own-OneDrive file (the same sealed id <c>files/{itemId}/pdf</c> takes), like the drive text route.</summary>
        [HttpGet("files/{itemId}/text")]
        public Task<IActionResult> FileText(Guid accountId, string itemId, [FromQuery] int? offset = null, [FromQuery] int? maxChars = null, [FromQuery] string? sheet = null, [FromQuery] int? rowOffset = null, [FromQuery] int? maxRows = null, CancellationToken ct = default) =>
            TextRead(accountId, ids => $"files/{E(ChatsController.DriveItemId(ids, itemId))}/text", offset, maxChars, sheet, rowOffset, maxRows, ct);

        /// <summary>The request body (raw file bytes, up to 25 MB) as text; <paramref name="name"/> is the file name whose extension picks the type. For files that are not in a drive.</summary>
        [HttpPost("convert/text")]
        [RequestSizeLimit(TeamsFileRequests.MaxTextConvertBytes)]
        public Task<IActionResult> ConvertText(Guid accountId, [FromQuery] string? name, CancellationToken ct = default)
        {
            if (!IsFileName(name))
                return Task.FromResult(Invalid("Invalid file name."));
            if (Request.ContentLength > TeamsFileRequests.MaxTextConvertBytes)
                return Task.FromResult(TextTooLarge());
            if (Request.ContentLength == 0)
                return Task.FromResult(Invalid("The body is empty."));
            // Chunked bodies carry no Content-Length, so the cap is also enforced while the body streams on.
            if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = TeamsFileRequests.MaxTextConvertBytes;
            return TextAsync(accountId, async (ids, c) =>
            {
                var content = new StreamContent(new LengthLimitedStream(Request.Body, TeamsFileRequests.MaxTextConvertBytes));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (Request.ContentLength is { } length)
                    content.Headers.ContentLength = length;
                using var upstream = await Reader.OpenPostStreamAsync(ids.UserId, accountId, $"convert/text?name={E(name!)}", content, c);
                if (upstream is null)
                    return NotFound();
                var data = await JsonSerializer.DeserializeAsync<JsonElement>(await upstream.Content.ReadAsStreamAsync(c), cancellationToken: c);
                return Ok(ResponseSealer.Seal(data, IdShape.FileText, ids));
            }, ct);
        }

        /// <summary>Files others shared with the user: <c>[driveItem]</c> with <c>shared</c>.</summary>
        [HttpGet("files/shared")]
        public Task<IActionResult> Shared(Guid accountId, [FromQuery] int? top = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"files/shared?top={Top(top, TeamsFileRequests.DefaultListTop)}", IdShape.DriveItemsInDrive, ct);

        /// <summary>Recently used files: <c>[driveItem]</c> with <c>lastModifiedBy</c>.</summary>
        [HttpGet("files/recent")]
        public Task<IActionResult> Recent(Guid accountId, [FromQuery] int? top = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"files/recent?top={Top(top, TeamsFileRequests.DefaultListTop)}", IdShape.DriveItemsInDrive, ct);

        /// <summary>Searches files. Body <c>{q, from, size, teamId}</c> (teamId sealed); the query stays in the body.</summary>
        [HttpPost("files/search")]
        public Task<IActionResult> SearchFiles(Guid accountId, [FromBody] SearchRequest? body, CancellationToken ct = default) => SearchAsync(accountId, "files/search", body, true, ct);

        /// <summary>Searches files, mail, chats and channel messages. Body <c>{q, types, from, size, teamId}</c> (teamId sealed); the query stays in the body.</summary>
        [HttpPost("search")]
        public Task<IActionResult> Search(Guid accountId, [FromBody] SearchRequest? body, CancellationToken ct = default) => SearchAsync(accountId, "search", body, false, ct);

        /// <summary>The newest messages of a channel (replies excluded).</summary>
        [HttpGet("teams/{teamId}/channels/{channelId}/messages")]
        public Task<IActionResult> Messages(Guid accountId, string teamId, string channelId, [FromQuery] int? top = null, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"{ChannelMessages(ids, teamId, channelId)}?top={Top(top, TeamsFileRequests.DefaultMessageTop)}", IdShape.ChannelMessages, ct);

        /// <summary>The replies to a channel message.</summary>
        [HttpGet("teams/{teamId}/channels/{channelId}/messages/{messageId}/replies")]
        public Task<IActionResult> Replies(Guid accountId, string teamId, string channelId, string messageId, [FromQuery] int? top = null, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"{ChannelMessages(ids, teamId, channelId)}/{E(ChannelMessage(ids, messageId))}/replies?top={Top(top, TeamsFileRequests.DefaultMessageTop)}", IdShape.ChannelMessages, ct);

        private static bool IsFileName(string? name) =>
            name is { Length: > 0 and <= 255 } && !name.Any(c => char.IsControl(c) || c is '/' or '\\' or '%') && !PathIds.HasDotSegment(name);

        private Task<IActionResult> SearchAsync(Guid accountId, string path, SearchRequest? body, bool filesOnly, CancellationToken ct)
        {
            var (search, error) = TeamsFileRequests.ValidateSearch(body, filesOnly);
            if (search is null)
                return Task.FromResult(Invalid(error!));
            Response.Headers.CacheControl = "no-store";
            return RunAsync(accountId, async (ids, c) =>
            {
                var team = search.TeamId is null ? null : Team(ids, search.TeamId);
                return Ok(ResponseSealer.Seal(await Reader.PostAsync(ids.UserId, accountId, path, search.ToBody(team), c), IdShape.Search, ids));
            }, ct);
        }

        private Task<IActionResult> TextRead(Guid accountId, Func<IdScope, string> path, int? offset, int? maxChars, string? sheet, int? rowOffset, int? maxRows, CancellationToken ct)
        {
            if (offset is < 0 || rowOffset is < 0)
                return Task.FromResult(Invalid("offset and rowOffset must not be negative."));
            if (sheet is not null && !TeamsFileRequests.IsSheetName(sheet))
                return Task.FromResult(Invalid("Invalid sheet name."));
            var query = $"max_chars={TeamsFileRequests.Clamp(maxChars, TeamsFileRequests.DefaultMaxChars, 1, TeamsFileRequests.MaxMaxChars)}&max_rows={TeamsFileRequests.Clamp(maxRows, TeamsFileRequests.DefaultMaxRows, 1, TeamsFileRequests.MaxMaxRows)}";
            if (offset is { } o)
                query += $"&offset={o}";
            if (rowOffset is { } r)
                query += $"&row_offset={r}";
            if (sheet is not null)
                query += $"&sheet={E(sheet)}";
            return TextAsync(accountId, async (ids, c) => Ok(ResponseSealer.Seal(await Reader.ReadAsync(ids.UserId, accountId, $"{path(ids)}?{query}", c), IdShape.FileText, ids)), ct);
        }

        // Text answers map the service's size and type refusals to 413 / 415 like the PDF routes, and a failing extraction to 502.
        private Task<IActionResult> TextAsync(Guid accountId, Func<IdScope, CancellationToken, Task<IActionResult>> action, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) =>
            {
                try
                {
                    return await action(ids, c);
                }
                catch (Exception ex) when (IsUploadTooLarge(ex))
                {
                    return TextTooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.RequestEntityTooLarge)
                {
                    return TextTooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.UnsupportedMediaType)
                {
                    return StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "unsupported_type", message = "This file type cannot be read as text." });
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
                {
                    return StatusCode(StatusCodes.Status502BadGateway, new { code = "text_failed", message = "The file text could not be read." });
                }
            }, ct);

        private IActionResult TextTooLarge() =>
            StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "too_large", message = "The file is larger than the text limit." });
    }
}
