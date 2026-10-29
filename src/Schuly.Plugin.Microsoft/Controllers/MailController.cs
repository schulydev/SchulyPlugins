using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Full mail access for the current user's linked Microsoft accounts. The fixed segments (folders, send, drafts) are excluded from <c>{messageId}</c> by a route constraint, so they can never be read as an id.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}/mail")]
    public class MailController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader) : MicrosoftControllerBase(userContext, linkedAccounts, reader)
    {
        private const int MaxRecipients = 100;
        private const int MaxRequestBytes = 5 * 1024 * 1024;
        private static readonly Regex IdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);

        [HttpGet("folders")]
        public Task<IActionResult> Folders(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "mail/folders", ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}")]
        public Task<IActionResult> Message(Guid accountId, string messageId, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            ReadAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}", ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/attachments")]
        public Task<IActionResult> Attachments(Guid accountId, string messageId, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            ReadAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/attachments", ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/attachments/{attachmentId}")]
        public Task<IActionResult> Attachment(Guid accountId, string messageId, string attachmentId, CancellationToken ct = default)
        {
            if (!PathIds.IsSafe(IdPattern, messageId) || !PathIds.IsSafe(IdPattern, attachmentId))
                return Task.FromResult(Invalid("Invalid message or attachment id."));
            return RunAsync(accountId, async (userId, c) =>
            {
                var file = await Reader.GetBinaryAsync(userId, accountId, $"mail/{Uri.EscapeDataString(messageId)}/attachments/{Uri.EscapeDataString(attachmentId)}", c);
                if (file.Bytes is null)
                    return NotFound();
                // Untrusted bytes on the app origin: forced download, sandboxed, a safe type, never the upstream disposition.
                AttachmentResponse.Apply(Response, file.ContentDisposition);
                return File(file.Bytes, AttachmentResponse.SafeContentType(file.ContentType));
            }, ct);
        }

        [HttpPost("send")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> Send(Guid accountId, [FromBody] MailMessageRequest? request, CancellationToken ct = default) =>
            Validate(request) is { } error ? Task.FromResult(error) :
            NoContentAsync(accountId, "mail/send", request!, ct);

        [HttpPost("drafts")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> CreateDraft(Guid accountId, [FromBody] MailMessageRequest? request, CancellationToken ct = default) =>
            Validate(request) is { } error ? Task.FromResult(error) :
            PostAsync(accountId, "mail/drafts", request!, ct);

        [HttpPost("drafts/{messageId}/send")]
        public Task<IActionResult> SendDraft(Guid accountId, string messageId, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            RunAsync(accountId, async (userId, c) =>
            {
                await Reader.PostEmptyAsync(userId, accountId, $"mail/drafts/{Uri.EscapeDataString(messageId)}/send", c);
                return NoContent();
            }, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/reply")]
        public Task<IActionResult> Reply(Guid accountId, string messageId, [FromBody] MailReplyRequest? request, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            request?.Comment is null ? Task.FromResult(Invalid("A comment is required.")) :
            NoContentAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/reply", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/forward")]
        public Task<IActionResult> Forward(Guid accountId, string messageId, [FromBody] MailForwardRequest? request, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            request?.To is not { Count: > 0 and <= MaxRecipients } ? Task.FromResult(Invalid($"1-{MaxRecipients} recipients are required.")) :
            NoContentAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/forward", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/read")]
        public Task<IActionResult> MarkRead(Guid accountId, string messageId, [FromBody] MailReadRequest? request, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            request is null ? Task.FromResult(Invalid("read is required.")) :
            PostAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/read", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/flag")]
        public Task<IActionResult> Flag(Guid accountId, string messageId, [FromBody] MailFlagRequest? request, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            request?.Status is not ("flagged" or "complete" or "notFlagged") ? Task.FromResult(Invalid("status must be flagged, complete or notFlagged.")) :
            PostAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/flag", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/move")]
        public Task<IActionResult> Move(Guid accountId, string messageId, [FromBody] MailMoveRequest? request, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            request?.Folder is not { } folder || !PathIds.IsSafe(IdPattern, folder) ? Task.FromResult(Invalid("Invalid folder.")) :
            PostAsync(accountId, $"mail/{Uri.EscapeDataString(messageId)}/move", request, ct);

        [HttpDelete("{messageId:regex(^(?!(folders|send|drafts)$).+$)}")]
        public Task<IActionResult> Delete(Guid accountId, string messageId, CancellationToken ct = default) =>
            !PathIds.IsSafe(IdPattern, messageId) ? Task.FromResult(Invalid("Invalid message id.")) :
            RunAsync(accountId, async (userId, c) =>
            {
                await Reader.DeleteAsync(userId, accountId, $"mail/{Uri.EscapeDataString(messageId)}", c);
                return NoContent();
            }, ct);

        private IActionResult? Validate(MailMessageRequest? request)
        {
            if (request is null || request.Subject is null || request.Body is null)
                return Invalid("subject and body are required.");
            var recipients = (request.To?.Count ?? 0) + (request.Cc?.Count ?? 0) + (request.Bcc?.Count ?? 0);
            if (recipients is 0 or > MaxRecipients)
                return Invalid($"1-{MaxRecipients} recipients are required.");
            return null;
        }

        private Task<IActionResult> PostAsync(Guid accountId, string path, object body, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) => Ok(await Reader.PostAsync(userId, accountId, path, body, c)), ct);

        private Task<IActionResult> NoContentAsync(Guid accountId, string path, object body, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) =>
            {
                await Reader.PostAsync(userId, accountId, path, body, c);
                return NoContent();
            }, ct);
    }

    public record MailAttachmentRequest(string? Name, string? ContentType, string? ContentBase64);

    public record MailMessageRequest(List<string>? To, List<string>? Cc, List<string>? Bcc, string? Subject, string? Body, bool Html, List<MailAttachmentRequest>? Attachments);

    public record MailReplyRequest(string? Comment, bool ReplyAll);

    public record MailForwardRequest(List<string>? To, string? Comment);

    public record MailReadRequest(bool Read);

    public record MailFlagRequest(string? Status);

    public record MailMoveRequest(string? Folder);
}
