using System.Net.Http.Headers;
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
    /// <summary>Office files (Word, Excel, PowerPoint, OpenDocument, RTF, CSV) as PDF for the web viewer, converted by Microsoft 365. Answers are <c>application/pdf</c>, inline, sandboxed and never stored; 415 <c>unsupported_type</c> when the type can't be converted, 502 <c>conversion_failed</c> when Microsoft 365 failed.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}")]
    public class FilePdfController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private static readonly Regex MailIdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);

        /// <summary>A OneDrive item (an <c>id</c> from the files list), converted where it lives.</summary>
        [HttpGet("files/{itemId}/pdf")]
        public Task<IActionResult> DriveItem(Guid accountId, string itemId, CancellationToken ct = default) =>
            PdfAsync(accountId, (ids, c) => Reader.OpenStreamAsync(ids.UserId, accountId, $"files/{Uri.EscapeDataString(ChatsController.DriveItemId(ids, itemId))}/pdf", c), ct);

        /// <summary>A file shared in a chat message (<c>files[].id</c>), converted where it lives (SharePoint / OneDrive).</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/files/{attachmentId}/pdf")]
        public Task<IActionResult> ChatFile(Guid accountId, string chatId, string messageId, string attachmentId, CancellationToken ct = default) =>
            PdfAsync(accountId, (ids, c) => Reader.OpenStreamAsync(ids.UserId, accountId, ChatsController.FilePath(ids, chatId, messageId, attachmentId) + "/pdf", c), ct);

        /// <summary>A mail attachment (up to 25 MB), converted through a temporary file in the account's app folder that the service deletes right after.</summary>
        [HttpGet("mail/{messageId}/attachments/{attachmentId}/pdf")]
        public Task<IActionResult> MailAttachment(Guid accountId, string messageId, string attachmentId, CancellationToken ct = default) =>
            PdfAsync(accountId, (ids, c) => Reader.OpenStreamAsync(ids.UserId, accountId, $"mail/{Uri.EscapeDataString(OpenSafe(ids, IdKinds.MailMessage, messageId, MailIdPattern))}/attachments/{Uri.EscapeDataString(OpenSafe(ids, IdKinds.MailAttachment, attachmentId, MailIdPattern))}/pdf", c), ct);

        /// <summary>The request body (the raw file bytes, up to 105 MB) converted to PDF; <paramref name="name"/> is the file name, whose extension picks the type. For files that aren't in a drive, such as school documents.</summary>
        [HttpPost("convert/pdf")]
        [RequestSizeLimit(PdfPreview.MaxConvertBytes)]
        public Task<IActionResult> Convert(Guid accountId, [FromQuery] string? name, CancellationToken ct = default)
        {
            if (!PdfPreview.IsConvertibleName(name))
                return Task.FromResult<IActionResult>(PdfUnsupported());
            if (Request.ContentLength > PdfPreview.MaxConvertBytes)
                return Task.FromResult(PdfTooLarge());
            if (Request.ContentLength == 0)
                return Task.FromResult(Invalid("The body is empty."));
            // Chunked bodies carry no Content-Length, so the cap is also enforced while the body streams on.
            if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = PdfPreview.MaxConvertBytes;
            return PdfAsync(accountId, (ids, c) =>
            {
                var content = new StreamContent(new LengthLimitedStream(Request.Body, PdfPreview.MaxConvertBytes));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (Request.ContentLength is { } length)
                    content.Headers.ContentLength = length;
                return Reader.OpenPostStreamAsync(ids.UserId, accountId, $"convert/pdf?name={Uri.EscapeDataString(name!)}", content, c);
            }, ct);
        }
    }
}
