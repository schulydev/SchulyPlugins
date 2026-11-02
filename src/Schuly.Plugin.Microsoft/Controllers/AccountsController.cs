using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Microsoft 365 data of the current user's linked Microsoft accounts (listing and unlinking live in core: <c>/api/me/linked-accounts</c>). Every id in and out is sealed (<see cref="IdSealer"/>).</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts")]
    public class AccountsController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);
        private static readonly Regex FolderPattern = new("^[A-Za-z0-9=_\\-+/.]{1,512}$", RegexOptions.Compiled);
        private static readonly Regex UserIdPattern = new("^[A-Za-z0-9@._-]{1,200}$", RegexOptions.Compiled);

        /// <summary>OneDrive paths are names, never ids: <c>root</c> or <c>root:/Folder/Sub:</c>.</summary>
        private static readonly Regex DrivePathPattern = new("^root(?::/[^:]{1,1000}:)?$", RegexOptions.Compiled);

        /// <summary>Graph's well-known mail folder names, accepted as they are instead of a sealed folder id.</summary>
        public static readonly string[] WellKnownFolders = ["inbox", "drafts", "sentitems", "deleteditems", "archive", "junkemail", "outbox", "msgfolderroot", "clutter", "conflicts", "conversationhistory", "localfailures", "recoverableitemsdeletions", "scheduled", "searchfolders", "serverfailures", "syncissues"];

        /// <summary>A mail folder parameter: a well-known name, or a sealed folder id opened (and path-checked) for this user and account.</summary>
        public static string OpenFolder(IdScope ids, string? folder) =>
            folder is not null && WellKnownFolders.Contains(folder.ToLowerInvariant()) ? folder.ToLowerInvariant() : OpenSafe(ids, IdKinds.MailFolder, folder, FolderPattern);

        [HttpGet("{accountId:guid}/mail")]
        public Task<IActionResult> Mail(Guid accountId, [FromQuery] string folder = "inbox", [FromQuery] int top = 25, [FromQuery] int skip = 0, [FromQuery] bool unread = false, [FromQuery] string? search = null, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"mail?folder={Uri.EscapeDataString(OpenFolder(ids, folder))}&top={Math.Clamp(top, 1, 500)}&skip={Math.Clamp(skip, 0, 10000)}&unread={unread.ToString().ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"), IdShape.MailMessages, ct);

        [HttpGet("{accountId:guid}/calendar")]
        public Task<IActionResult> Calendar(Guid accountId, [FromQuery] int top = 25, [FromQuery] DateTimeOffset? start = null, [FromQuery] DateTimeOffset? end = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"calendar?top={Math.Clamp(top, 1, 500)}" + (start is { } s && end is { } e ? $"&start={Uri.EscapeDataString(s.UtcDateTime.ToString("o"))}&end={Uri.EscapeDataString(e.UtcDateTime.ToString("o"))}" : ""), IdShape.CalendarEvents, ct);

        [HttpGet("{accountId:guid}/files")]
        public Task<IActionResult> Files(Guid accountId, [FromQuery] string path = "root", [FromQuery] int top = 100, CancellationToken ct = default) =>
            PathIds.HasDotSegment(path) || !DrivePathPattern.IsMatch(path) ? Task.FromResult(Invalid("Invalid path.")) :
            ReadAsync(accountId, $"files?path={Uri.EscapeDataString(path)}&top={Math.Clamp(top, 1, 1000)}", IdShape.DriveItems, ct);

        [HttpGet("{accountId:guid}/chats")]
        public Task<IActionResult> Chats(Guid accountId, [FromQuery] int top = 25, CancellationToken ct = default) =>
            ReadAsync(accountId, $"chats?top={Math.Clamp(top, 1, 200)}", IdShape.Chats, ct);

        [HttpGet("{accountId:guid}/notebooks")]
        public Task<IActionResult> Notebooks(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "notebooks", IdShape.Notebooks, ct);

        [HttpGet("{accountId:guid}/me")]
        public Task<IActionResult> Me(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "me", IdShape.Me, ct);

        [HttpGet("{accountId:guid}/chats/{chatId}/messages")]
        public Task<IActionResult> ChatMessages(Guid accountId, string chatId, [FromQuery] int top = 30, CancellationToken ct = default) =>
            ReadAsync(accountId, ids => $"chats/{Uri.EscapeDataString(OpenSafe(ids, IdKinds.Chat, chatId, ChatIdPattern))}/messages?top={Math.Clamp(top, 1, 300)}", IdShape.ChatMessages, ct);

        [HttpPost("{accountId:guid}/chats/{chatId}/read")]
        public Task<IActionResult> MarkChatRead(Guid accountId, string chatId, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                await Reader.PostEmptyAsync(ids.UserId, accountId, $"chats/{Uri.EscapeDataString(OpenSafe(ids, IdKinds.Chat, chatId, ChatIdPattern))}/read", c);
                return NoContent();
            }, ct);

        /// <summary>A Teams user's photo; <paramref name="userId"/> is a sealed user id (<c>members[].userId</c>, <c>from.user.id</c>, <c>me.id</c>).</summary>
        [HttpGet("{accountId:guid}/photos/{userId}")]
        public Task<IActionResult> Photo(Guid accountId, string userId, [FromQuery] int size = 64, CancellationToken ct = default) =>
            RunAsync(accountId, async (ids, c) =>
            {
                var photo = await Reader.GetPhotoAsync(ids.UserId, accountId, OpenSafe(ids, IdKinds.User, userId, UserIdPattern), Math.Clamp(size, 16, 648), c);
                if (photo.Bytes is null)
                    return NotFound();
                Response.Headers.CacheControl = "private, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(photo.Bytes, photo.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? photo.ContentType : "image/jpeg");
            }, ct);
    }

}
