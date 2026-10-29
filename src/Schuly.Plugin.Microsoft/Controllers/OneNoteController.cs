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
    public class OneNoteController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader) : MicrosoftControllerBase(userContext, linkedAccounts, reader)
    {
        private const int MaxRequestBytes = 2 * 1024 * 1024;
        private const int MaxTitleLength = 255;
        private const int MaxOutlines = 50;
        private const int MaxNameLength = 255;
        private const int MaxQueryLength = 100;

        [HttpGet("notebooks")]
        public Task<IActionResult> Notebooks(Guid accountId, [FromQuery] bool teams = true, CancellationToken ct = default) =>
            ReadAsync(accountId, $"onenote/notebooks?teams={teams.ToString().ToLowerInvariant()}", ct);

        [HttpGet("notebooks/{notebookId}/sections")]
        public Task<IActionResult> Sections(Guid accountId, string notebookId, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(notebookId) ? Task.FromResult(Invalid("Invalid notebook id.")) :
            ReadAsync(accountId, $"onenote/notebooks/{Uri.EscapeDataString(notebookId)}/sections", ct);

        [HttpGet("sections/{sectionId}/pages")]
        public Task<IActionResult> Pages(Guid accountId, string sectionId, [FromQuery] int top = 50, [FromQuery] int skip = 0, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(sectionId) ? Task.FromResult(Invalid("Invalid section id.")) :
            ReadAsync(accountId, $"onenote/sections/{Uri.EscapeDataString(sectionId)}/pages?top={Math.Clamp(top, 1, 100)}&skip={Math.Clamp(skip, 0, 5000)}", ct);

        [HttpPost("sections/{sectionId}/pages")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> CreatePage(Guid accountId, string sectionId, [FromBody] OneNoteCreatePageRequest? request, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(sectionId) ? Task.FromResult(Invalid("Invalid section id.")) :
            request is null || string.IsNullOrWhiteSpace(request.Title) || request.Title.Length > MaxTitleLength ? Task.FromResult(Invalid($"title is required (1-{MaxTitleLength} characters).")) :
            RunAsync(accountId, async (userId, c) => StatusCode(201, await Reader.PostAsync(userId, accountId, $"onenote/sections/{Uri.EscapeDataString(sectionId)}/pages", request, c)), ct);

        [HttpGet("pages/{pageId}")]
        public Task<IActionResult> Page(Guid accountId, string pageId, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(pageId) ? Task.FromResult(Invalid("Invalid page id.")) :
            ReadAsync(accountId, $"onenote/pages/{Uri.EscapeDataString(pageId)}", ct);

        [HttpPatch("pages/{pageId}")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> UpdatePage(Guid accountId, string pageId, [FromBody] OneNotePageUpdateRequest? request, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(pageId) ? Task.FromResult(Invalid("Invalid page id.")) :
            request is null ? Task.FromResult(Invalid("A body is required.")) :
            request.Title is { } title && (title.Length is 0 or > MaxTitleLength) ? Task.FromResult(Invalid($"title must be 1-{MaxTitleLength} characters.")) :
            request.Outlines is { Count: > MaxOutlines } ? Task.FromResult(Invalid($"At most {MaxOutlines} outlines can be replaced at once.")) :
            request.Outlines is { } outlines && outlines.Any(o => string.IsNullOrEmpty(o.Id) || o.Id.Length > 300 || o.Html is null) ? Task.FromResult(Invalid("Every outline needs an id and html.")) :
            RunAsync(accountId, async (userId, c) => Ok(await Reader.PatchAsync(userId, accountId, $"onenote/pages/{Uri.EscapeDataString(pageId)}", request, c)), ct);

        [HttpDelete("pages/{pageId}")]
        public Task<IActionResult> DeletePage(Guid accountId, string pageId, CancellationToken ct = default) =>
            !OneNoteIds.IsValid(pageId) ? Task.FromResult(Invalid("Invalid page id.")) :
            RunAsync(accountId, async (userId, c) =>
            {
                await Reader.DeleteAsync(userId, accountId, $"onenote/pages/{Uri.EscapeDataString(pageId)}", c);
                return NoContent();
            }, ct);

        /// <summary>A page image (<c>kind=image</c>, default: photo lane, cached, inline) or attachment (<c>kind=file</c>: never cached, forced download; <paramref name="name"/> is the suggested file name).</summary>
        [HttpGet("resources/{resourceId}")]
        public Task<IActionResult> Resource(Guid accountId, string resourceId, [FromQuery] string kind = "image", [FromQuery] string? name = null, CancellationToken ct = default)
        {
            if (!OneNoteIds.IsValid(resourceId))
                return Task.FromResult(Invalid("Invalid resource id."));
            if (kind is not ("image" or "file"))
                return Task.FromResult(Invalid("kind must be image or file."));
            if (name is { Length: > MaxNameLength })
                return Task.FromResult(Invalid($"name must be at most {MaxNameLength} characters."));

            var escaped = Uri.EscapeDataString(resourceId);
            if (kind == "image")
            {
                return RunAsync(accountId, async (userId, c) =>
                {
                    var image = await Reader.GetImageAsync(userId, accountId, $"onenote|{resourceId}", $"onenote/resources/{escaped}?kind=image", c);
                    if (image.Bytes is null)
                        return NotFound();
                    if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) || image.ContentType.Contains("svg", StringComparison.OrdinalIgnoreCase))
                        return StatusCode(415, new { code = "invalid_request", message = "The resource is not an image." });
                    Response.Headers.CacheControl = "private, max-age=3600";
                    Response.Headers["X-Content-Type-Options"] = "nosniff";
                    return File(image.Bytes, image.ContentType);
                }, ct);
            }

            var path = $"onenote/resources/{escaped}?kind=file" + (string.IsNullOrEmpty(name) ? "" : $"&name={Uri.EscapeDataString(name)}");
            return RunAsync(accountId, async (userId, c) =>
            {
                var file = await Reader.GetBinaryAsync(userId, accountId, path, c);
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
            notebook is not null && !OneNoteIds.IsValid(notebook) ? Task.FromResult(Invalid("Invalid notebook id.")) :
            ReadAsync(accountId, $"onenote/search?q={Uri.EscapeDataString(q.Trim())}" + (notebook is null ? "" : $"&notebook={Uri.EscapeDataString(notebook)}") + $"&top={Math.Clamp(top, 1, 50)}", ct);
    }

    public record OneNoteCreatePageRequest(string? Title, string? Html);

    public record OneNoteOutlineUpdate(string? Id, string? Html);

    public record OneNotePageUpdateRequest(string? Title, List<OneNoteOutlineUpdate>? Outlines, string? Append);
}
