using System.Net;
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
    public class FilePdfController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader) : MicrosoftControllerBase(userContext, linkedAccounts, reader)
    {
        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);
        private static readonly Regex MessageIdPattern = new("^[A-Za-z0-9_-]{1,64}$", RegexOptions.Compiled);
        private static readonly Regex AttachmentIdPattern = new("^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled);
        private static readonly Regex MailIdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);

        /// <summary>A OneDrive item (an <c>id</c> from the files list), converted where it lives.</summary>
        [HttpGet("files/{itemId}/pdf")]
        public Task<IActionResult> DriveItem(Guid accountId, string itemId, CancellationToken ct = default) =>
            !EmojiRequests.IsDriveItemId(itemId) ? Task.FromResult(Invalid("Invalid item id.")) :
            PdfAsync(accountId, (userId, c) => Reader.OpenStreamAsync(userId, accountId, $"files/{Uri.EscapeDataString(itemId)}/pdf", c), ct);

        /// <summary>A file shared in a chat message (<c>files[].id</c>), converted where it lives (SharePoint / OneDrive).</summary>
        [HttpGet("chats/{chatId}/messages/{messageId}/files/{attachmentId}/pdf")]
        public Task<IActionResult> ChatFile(Guid accountId, string chatId, string messageId, string attachmentId, CancellationToken ct = default)
        {
            if (!PathIds.IsSafe(ChatIdPattern, chatId) || !PathIds.IsSafe(MessageIdPattern, messageId) || !PathIds.IsSafe(AttachmentIdPattern, attachmentId))
                return Task.FromResult(Invalid("Invalid chat, message or file id."));
            return PdfAsync(accountId, (userId, c) => Reader.OpenStreamAsync(userId, accountId, $"chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}/files/{Uri.EscapeDataString(attachmentId)}/pdf", c), ct);
        }

        /// <summary>A mail attachment (up to 25 MB), converted through a temporary file in the account's app folder that the service deletes right after.</summary>
        [HttpGet("mail/{messageId}/attachments/{attachmentId}/pdf")]
        public Task<IActionResult> MailAttachment(Guid accountId, string messageId, string attachmentId, CancellationToken ct = default)
        {
            if (!PathIds.IsSafe(MailIdPattern, messageId) || !PathIds.IsSafe(MailIdPattern, attachmentId))
                return Task.FromResult(Invalid("Invalid message or attachment id."));
            return PdfAsync(accountId, (userId, c) => Reader.OpenStreamAsync(userId, accountId, $"mail/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}/pdf", c), ct);
        }

        /// <summary>The request body (the raw file bytes, up to 105 MB) converted to PDF; <paramref name="name"/> is the file name, whose extension picks the type. For files that aren't in a drive, such as school documents.</summary>
        [HttpPost("convert/pdf")]
        [RequestSizeLimit(PdfPreview.MaxConvertBytes)]
        public Task<IActionResult> Convert(Guid accountId, [FromQuery] string? name, CancellationToken ct = default)
        {
            if (!PdfPreview.IsConvertibleName(name))
                return Task.FromResult<IActionResult>(Unsupported());
            if (Request.ContentLength > PdfPreview.MaxConvertBytes)
                return Task.FromResult(TooLarge());
            if (Request.ContentLength == 0)
                return Task.FromResult(Invalid("The body is empty."));
            // Chunked bodies carry no Content-Length, so the cap is also enforced while the body streams on.
            if (HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = PdfPreview.MaxConvertBytes;
            return PdfAsync(accountId, (userId, c) =>
            {
                var content = new StreamContent(new LengthLimitedStream(Request.Body, PdfPreview.MaxConvertBytes));
                content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                if (Request.ContentLength is { } length)
                    content.Headers.ContentLength = length;
                return Reader.OpenPostStreamAsync(userId, accountId, $"convert/pdf?name={Uri.EscapeDataString(name!)}", content, c);
            }, ct);
        }

        private Task<IActionResult> PdfAsync(Guid accountId, Func<Guid, CancellationToken, Task<HttpResponseMessage?>> open, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) =>
            {
                HttpResponseMessage? upstream;
                try
                {
                    upstream = await open(userId, c);
                }
                catch (Exception ex) when (IsTooLarge(ex))
                {
                    return TooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.UnsupportedMediaType)
                {
                    return Unsupported();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.RequestEntityTooLarge)
                {
                    return TooLarge();
                }
                catch (HttpRequestException ex) when (ex.StatusCode is null or >= HttpStatusCode.InternalServerError)
                {
                    return ConversionFailed();
                }
                if (upstream is null)
                    return NotFound();
                HttpContext.Response.RegisterForDispose(upstream);
                // Only a PDF is ever served inline; anything else the service could answer is refused.
                if (!string.Equals(upstream.Content.Headers.ContentType?.MediaType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                    return ConversionFailed();
                AttachmentResponse.ApplyInlinePdf(Response, upstream.Content.Headers.ContentDisposition?.ToString());
                if (upstream.Content.Headers.ContentLength is { } length)
                    Response.ContentLength = length;
                return File(await upstream.Content.ReadAsStreamAsync(c), "application/pdf", enableRangeProcessing: false);
            }, ct);

        private static bool IsTooLarge(Exception? ex)
        {
            for (; ex is not null; ex = ex.InnerException)
                if (ex is LengthLimitedStream.TooLargeException || (ex is BadHttpRequestException bad && bad.StatusCode == StatusCodes.Status413PayloadTooLarge))
                    return true;
            return false;
        }

        private ObjectResult Unsupported() =>
            StatusCode(StatusCodes.Status415UnsupportedMediaType, new { code = "unsupported_type", message = "This file type cannot be converted to PDF." });

        private IActionResult TooLarge() =>
            StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = "invalid_request", message = "The file is larger than the conversion limit." });

        private ObjectResult ConversionFailed() =>
            StatusCode(StatusCodes.Status502BadGateway, new { code = "conversion_failed", message = "Microsoft 365 could not convert the file." });
    }
}
