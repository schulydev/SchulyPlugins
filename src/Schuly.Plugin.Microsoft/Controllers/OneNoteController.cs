using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>OneNote for the current user's linked Microsoft accounts: notebooks, section tree, pages (read, create, edit, delete), page images and attachments, and title search. Every id is an opaque scoped id (<see cref="OneNoteIds"/>).</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}/onenote")]
    public class OneNoteController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private const int MaxRequestBytes = 2 * 1024 * 1024;
        private const int MaxTitleLength = 255;
        private const int MaxOutlines = 50;
        private const int MaxNameLength = 255;
        private const int MaxQueryLength = 100;

        /// <summary>The escaped OneNote scoped id behind a sealed one, checked by <see cref="OneNoteIds"/>.</summary>
        private static string Open(IdScope ids, string kind, string sealedId)
        {
            var id = ids.Open(kind, sealedId);
            return OneNoteIds.IsValid(id) ? Uri.EscapeDataString(id) : throw new UnsafeIdException();
        }

        [HttpGet("notebooks")]
        public Task<IActionResult> Notebooks(Guid accountId, [FromQuery] bool teams = true, CancellationToken ct = default) =>
            ReadAsync(accountId, $"onenote/notebooks?teams={teams.ToString().ToLowerInvariant()}", IdShape.Notebooks, ct);

        [HttpGet("notebooks/{notebookId}/sections")]
        public Task<IActionResult> Sections(Guid accountId, string notebookId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"onenote/notebooks/{Open(ids, IdKinds.Notebook, notebookId)}/sections", IdShape.OneNoteSections, ct);

        [HttpGet("sections/{sectionId}/pages")]
        public Task<IActionResult> Pages(Guid accountId, string sectionId, [FromQuery] int top = 50, [FromQuery] int skip = 0, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"onenote/sections/{Open(ids, IdKinds.Section, sectionId)}/pages?top={Math.Clamp(top, 1, 100)}&skip={Math.Clamp(skip, 0, 5000)}", IdShape.OneNotePages, ct);

        [HttpPost("sections/{sectionId}/pages")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> CreatePage(Guid accountId, string sectionId, [FromBody] OneNoteCreatePageRequest? request, CancellationToken ct = default) =>
            request is null || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength ? Task.FromResult(Invalid($"title is required (1-{MaxTitleLength} characters).")) :
            RunAsync(accountId, async (ids, c) => StatusCode(201, ResponseSealer.Seal(await Reader.PostAsync(ids.UserId, accountId, $"onenote/sections/{Open(ids, IdKinds.Section, sectionId)}/pages", request, c), IdShape.OneNotePage, ids)), ct);

        [HttpGet("pages/{pageId}")]
        public Task<IActionResult> Page(Guid accountId, string pageId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"onenote/pages/{Open(ids, IdKinds.Page, pageId)}", IdShape.OneNotePage, ct);

        [HttpPatch("pages/{pageId}")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> UpdatePage(Guid accountId, string pageId, [FromBody] OneNotePageUpdateRequest? request, CancellationToken ct = default) =>
            request is null ? Task.FromResult(Invalid("A body is required.")) :
            request.Title is { } title && (title.Length is 0 or > MaxTitleLength) ? Task.FromResult(Invalid($"title must be 1-{MaxTitleLength} characters.")) :
            request.Outlines is { Count: > MaxOutlines } ? Task.FromResult(Invalid($"At most {MaxOutlines} outlines can be replaced at once.")) :
            request.Outlines is { } outlines && outlines.Any(o => string.IsNullOrEmpty(o.Id) || o.Id.Length > 300 || o.Html is null) ? Task.FromResult(Invalid("Every outline needs an id and html.")) :
            RunAsync(accountId, async (ids, c) => Ok(ResponseSealer.Seal(await Reader.PatchAsync(ids.UserId, accountId, $"onenote/pages/{Open(ids, IdKinds.Page, pageId)}", request, c), IdShape.OneNotePage, ids)), ct);

        [HttpDelete("pages/{pageId}")]
        public Task<IActionResult> DeletePage(Guid accountId, string pageId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                await Reader.DeleteAsync(ids.UserId, accountId, $"onenote/pages/{Open(ids, IdKinds.Page, pageId)}", c);
                return NoContent();
            }, ct);

        /// <summary>A page image (<c>kind=image</c>, default: photo lane, cached, inline) or attachment (<c>kind=file</c>: never cached, forced download; <paramref name="name"/> is the suggested file name).</summary>
        [HttpGet("resources/{resourceId}")]
        public Task<IActionResult> Resource(Guid accountId, string resourceId, [FromQuery] string kind = "image", [FromQuery] string? name = null, CancellationToken ct = default)
        {
            if (kind is not ("image" or "file"))
                return Task.FromResult(Invalid("kind must be image or file."));
            if (name is { Length: > MaxNameLength })
                return Task.FromResult(Invalid($"name must be at most {MaxNameLength} characters."));

            if (kind == "image")
            {
                return RunAsync(accountId, async (ids, c) =>
                {
                    var escaped = Open(ids, IdKinds.Resource, resourceId);
                    var image = await Reader.GetImageAsync(ids.UserId, accountId, $"onenote|{escaped}", $"onenote/resources/{escaped}?kind=image", c);
                    if (image.Bytes is null)
                        return NotFound();
                    if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || image.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                        return StatusCode(415, new { code = "invalid_request", message = "The resource is not an image." });
                    Response.Headers.CacheControl = "private, max-age=3600";
                    Response.Headers["X-Content-Type-Options"] = "nosniff";
                    return File(image.Bytes, image.ContentType);
                }, ct);
            }

            return RunAsync(accountId, async (ids, c) =>
            {
                var path = $"onenote/resources/{Open(ids, IdKinds.Resource, resourceId)}?kind=file" + (string.IsNullOrEmpty(name) ? "" : $"&name={Uri.EscapeDataString(name)}");
                var file = await Reader.GetBinaryAsync(ids.UserId, accountId, path, c);
                if (file.Bytes is null)
                    return NotFound();
                AttachmentResponse.Apply(Response, file.ContentDisposition);
                return File(file.Bytes, AttachmentResponse.SafeContentType(file.ContentType));
            }, ct);
        }

        /// <summary>Page title search. The service has no content search; <c>partial</c> in the answer says a big class notebook was only searched in part.</summary>
        [HttpGet("search")]
        public Task<IActionResult> Search(Guid accountId, [FromQuery] string? q, [FromQuery] string? notebook, [FromQuery] int top = 25, CancellationToken ct = default) =>
            q is null || q.Trim().Length is 0 or > MaxQueryLength ? Task.FromResult(Invalid($"q must be 1-{MaxQueryLength} characters.")) :
            ReadAsync(accountId, ids => $"onenote/search?q={Uri.EscapeDataString(q.Trim())}" + (notebook is null ? "" : $"&notebook={Open(ids, IdKinds.Notebook, notebook)}") + $"&top={Math.Clamp(top, 1, 50)}", IdShape.OneNotePages, ct);
    }

    public record OneNoteCreatePageRequest(string? Title, string? Html);

    public record OneNoteOutlineUpdate(string? Id, string? Html);

    public record OneNotePageUpdateRequest(string? Title, List<OneNoteOutlineUpdate>? Outlines, string? Append);
}
