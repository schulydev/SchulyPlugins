using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Video and audio for a <c>&lt;video&gt;</c> element, which can't send the bearer token: a signed-in POST opens a ticket for one file (see <see cref="MediaTickets"/>), and the element plays the URL it answers. The bytes stream through with Range, so a player can seek and nothing holds the whole file.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft")]
    public class MediaStreamController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader, IdSealer sealer, MediaTickets tickets) : MicrosoftControllerBase(userContext, linkedAccounts, reader, sealer)
    {
        /// <summary>Opens a ticket for a file shared in a message.</summary>
        [HttpPost("accounts/{accountId:guid}/chats/{chatId}/messages/{messageId}/files/{attachmentId}/stream")]
        public Task<IActionResult> ChatFile(Guid accountId, string chatId, string messageId, string attachmentId, CancellationToken ct = default) =>
            OpenAsync(accountId, ids => ChatsController.FilePath(ids, chatId, messageId, attachmentId), ct);

        /// <summary>Opens a ticket for a drive file.</summary>
        [HttpPost("accounts/{accountId:guid}/drives/{driveId}/items/{itemId}/stream")]
        public Task<IActionResult> DriveItem(Guid accountId, string driveId, string itemId, CancellationToken ct = default) =>
            OpenAsync(accountId, ids => $"{TeamsFilesController.DriveItemPath(ids, driveId, itemId)}/content", ct);

        /// <summary>The ticket's bytes, whole or one range. Only video and audio, and with no ticket it does not exist.</summary>
        [AllowAnonymous]
        [HttpGet("media/{handle}")]
        public async Task<IActionResult> Play(string handle, CancellationToken ct = default)
        {
            var ticket = tickets.Find(handle, Request.Cookies[MediaTickets.CookieName]);
            if (ticket is null)
                return NotFound();
            return await RunForAsync(ticket.UserId, ticket.AccountId, (_, c) => StreamFileAsync(ticket.UserId, ticket.AccountId, ticket.Path, c, IsPlayable), ct);
        }

        private static bool IsPlayable(string type) => type.StartsWith("video/", StringComparison.Ordinal) || type.StartsWith("audio/", StringComparison.Ordinal);

        private Task<IActionResult> OpenAsync(Guid accountId, Func<IdScope, string> path, CancellationToken ct) =>
            RunAsync(accountId, (ids, _) =>
            {
                var (handle, secret) = tickets.Open(ids.UserId, accountId, path(ids));
                var url = $"/api/plugins/microsoft/media/{handle}";
                Response.Headers.CacheControl = "no-store";
                Response.Cookies.Append(MediaTickets.CookieName, secret, new CookieOptions { Path = url, HttpOnly = true, Secure = true, SameSite = SameSiteMode.Strict, MaxAge = MediaTickets.MaxLife });
                return Task.FromResult<IActionResult>(Ok(new { url }));
            }, ct);
    }
}
