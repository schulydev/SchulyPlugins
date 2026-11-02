using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Full mail access for the current user's linked Microsoft accounts. Message, attachment and folder ids are sealed. The fixed segments (folders, send, drafts) are excluded from <c>{messageId}</c> by a route constraint, so they can never be read as an id.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts/{accountId:guid}/mail")]
    public class MailController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private const int MaxRecipients = 100;
        private const int MaxRequestBytes = 5 * 1024 * 1024;
        private static readonly Regex IdPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);

        [HttpGet("folders")]
        public Task<IActionResult> Folders(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "mail/folders", IdShape.MailFolders, ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}")]
        public Task<IActionResult> Message(Guid accountId, string messageId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}", IdShape.MailMessages, ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/attachments")]
        public Task<IActionResult> Attachments(Guid accountId, string messageId, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}/attachments", IdShape.MailAttachments, ct);

        [HttpGet("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/attachments/{attachmentId}")]
        public Task<IActionResult> Attachment(Guid accountId, string messageId, string attachmentId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                var file = await Reader.GetBinaryAsync(ids.UserId, accountId, $"mail/{MessageSegment(ids, messageId)}/attachments/{AttachmentSegment(ids, attachmentId)}", c);
                if (file.Bytes is null)
                    return NotFound();
                // Untrusted bytes on the app origin: forced download, sandboxed, a safe type, never the upstream disposition.
                AttachmentResponse.Apply(Response, file.ContentDisposition);
                return File(file.Bytes, AttachmentResponse.SafeContentType(file.ContentType));
            }, ct);

        [HttpPost("send")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> Send(Guid accountId, [FromBody] MailMessageRequest? request, CancellationToken ct = default) =>
            Validate(request) is { } error ? Task.FromResult(error) :
            NoContentAsync(accountId, _ => "mail/send", request!, ct);

        [HttpPost("drafts")]
        [RequestSizeLimit(MaxRequestBytes)]
        public Task<IActionResult> CreateDraft(Guid accountId, [FromBody] MailMessageRequest? request, CancellationToken ct = default) =>
            Validate(request) is { } error ? Task.FromResult(error) :
            PostAsync(accountId, _ => "mail/drafts", request!, ct);

        [HttpPost("drafts/{messageId}/send")]
        public Task<IActionResult> SendDraft(Guid accountId, string messageId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                await Reader.PostEmptyAsync(ids.UserId, accountId, $"mail/drafts/{MessageSegment(ids, messageId)}/send", c);
                return NoContent();
            }, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/reply")]
        public Task<IActionResult> Reply(Guid accountId, string messageId, [FromBody] MailReplyRequest? request, CancellationToken ct = default) =>
            request?.Comment is null ? Task.FromResult(Invalid("A comment is required.")) :
            NoContentAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}/reply", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/forward")]
        public Task<IActionResult> Forward(Guid accountId, string messageId, [FromBody] MailForwardRequest? request, CancellationToken ct = default) =>
            request?.To is not { Count: > 0 and <= MaxRecipients } ? Task.FromResult(Invalid($"1-{MaxRecipients} recipients are required.")) :
            NoContentAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}/forward", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/read")]
        public Task<IActionResult> MarkRead(Guid accountId, string messageId, [FromBody] MailReadRequest? request, CancellationToken ct = default) =>
            request is null ? Task.FromResult(Invalid("read is required.")) :
            PostAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}/read", request, ct);

        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/flag")]
        public Task<IActionResult> Flag(Guid accountId, string messageId, [FromBody] MailFlagRequest? request, CancellationToken ct = default) =>
            request?.Status is not ("flagged" or "complete" or "notFlagged") ? Task.FromResult(Invalid("status must be flagged, complete or notFlagged.")) :
            PostAsync(accountId, ids => $"mail/{MessageSegment(ids, messageId)}/flag", request, ct);

        /// <summary>Moves a message; <c>folder</c> is a well-known name or a sealed folder id. Answers the message's new (sealed) id.</summary>
        [HttpPost("{messageId:regex(^(?!(folders|send|drafts)$).+$)}/move")]
        public Task<IActionResult> Move(Guid accountId, string messageId, [FromBody] MailMoveRequest? request, CancellationToken ct = default) =>
            string.IsNullOrEmpty(request?.Folder) ? Task.FromResult(Invalid("Invalid folder.")) :
            RunAsync(accountId, async (ids, c) => Ok(ResponseSealer.Seal(await Reader.PostAsync(ids.UserId, accountId, $"mail/{MessageSegment(ids, messageId)}/move", new MailMoveRequest(AccountsController.OpenFolder(ids, request.Folder)), c), IdShape.MailMessages, ids)), ct);

        [HttpDelete("{messageId:regex(^(?!(folders|send|drafts)$).+$)}")]
        public Task<IActionResult> Delete(Guid accountId, string messageId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                await Reader.DeleteAsync(ids.UserId, accountId, $"mail/{MessageSegment(ids, messageId)}", c);
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

        /// <summary>The escaped Graph message id behind a sealed one.</summary>
        private static string MessageSegment(IdScope ids, string messageId) => Uri.EscapeDataString(OpenSafe(ids, IdKinds.MailMessage, messageId, IdPattern));

        private static string AttachmentSegment(IdScope ids, string attachmentId) => Uri.EscapeDataString(OpenSafe(ids, IdKinds.MailAttachment, attachmentId, IdPattern));

        private Task<IActionResult> PostAsync(Guid accountId, Func<IdScope, string> path, object body, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) => Ok(ResponseSealer.Seal(await Reader.PostAsync(ids.UserId, accountId, path(ids), body, c), IdShape.MailMessages, ids)), ct);

        private Task<IActionResult> NoContentAsync(Guid accountId, Func<IdScope, string> path, object body, CancellationToken ct) =>
            RunAsync(accountId, async (ids, c) =>
            {
                await Reader.PostAsync(ids.UserId, accountId, path(ids), body, c);
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
