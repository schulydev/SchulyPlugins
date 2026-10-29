using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Microsoft 365 data of the current user's linked Microsoft accounts (listing and unlinking live in core: <c>/api/me/linked-accounts</c>).</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts")]
    public class AccountsController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftAccountReader reader) : ControllerBase
    {
        private static readonly Regex ChatIdPattern = new("^[A-Za-z0-9:@._=-]{1,300}$", RegexOptions.Compiled);
        private static readonly Regex UserIdPattern = new("^[A-Za-z0-9@._-]{1,200}$", RegexOptions.Compiled);

        [HttpGet("{accountId:guid}/mail")]
        public Task<IActionResult> Mail(Guid accountId, [FromQuery] string folder = "inbox", [FromQuery] int top = 25, [FromQuery] bool unread = false, [FromQuery] string? search = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"mail?folder={Uri.EscapeDataString(folder)}&top={Math.Clamp(top, 1, 500)}&unread={unread.ToString().ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"), ct);

        [HttpGet("{accountId:guid}/calendar")]
        public Task<IActionResult> Calendar(Guid accountId, [FromQuery] int top = 25, [FromQuery] DateTimeOffset? start = null, [FromQuery] DateTimeOffset? end = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"calendar?top={Math.Clamp(top, 1, 500)}" + (start is { } s && end is { } e ? $"&start={Uri.EscapeDataString(s.UtcDateTime.ToString("o"))}&end={Uri.EscapeDataString(e.UtcDateTime.ToString("o"))}" : ""), ct);

        [HttpGet("{accountId:guid}/files")]
        public Task<IActionResult> Files(Guid accountId, [FromQuery] string path = "root", [FromQuery] int top = 100, CancellationToken ct = default) =>
            ReadAsync(accountId, $"files?path={Uri.EscapeDataString(path)}&top={Math.Clamp(top, 1, 1000)}", ct);

        [HttpGet("{accountId:guid}/chats")]
        public Task<IActionResult> Chats(Guid accountId, [FromQuery] int top = 25, CancellationToken ct = default) =>
            ReadAsync(accountId, $"chats?top={Math.Clamp(top, 1, 200)}", ct);

        [HttpGet("{accountId:guid}/notebooks")]
        public Task<IActionResult> Notebooks(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "notebooks", ct);

        [HttpGet("{accountId:guid}/me")]
        public Task<IActionResult> Me(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "me", ct);

        [HttpGet("{accountId:guid}/chats/{chatId}/messages")]
        public Task<IActionResult> ChatMessages(Guid accountId, string chatId, [FromQuery] int top = 30, CancellationToken ct = default) =>
            !ChatIdPattern.IsMatch(chatId) ? Task.FromResult(Invalid("Invalid chat id.")) :
            ReadAsync(accountId, $"chats/{Uri.EscapeDataString(chatId)}/messages?top={Math.Clamp(top, 1, 300)}", ct);

        [HttpPost("{accountId:guid}/chats/{chatId}/messages")]
        public Task<IActionResult> SendChatMessage(Guid accountId, string chatId, [FromBody] SendChatMessageRequest? request, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId))
                return Task.FromResult(Invalid("Invalid chat id."));
            var text = request?.Text?.Trim();
            if (string.IsNullOrEmpty(text) || text.Length > 4000)
                return Task.FromResult(Invalid("Message text must be 1-4000 characters."));
            return RunAsync(accountId, async (userId, c) => Ok(await reader.PostAsync(userId, accountId, $"chats/{Uri.EscapeDataString(chatId)}/messages", new { text }, c)), ct);
        }

        [HttpPost("{accountId:guid}/chats/{chatId}/read")]
        public Task<IActionResult> MarkChatRead(Guid accountId, string chatId, CancellationToken ct = default)
        {
            if (!ChatIdPattern.IsMatch(chatId))
                return Task.FromResult(Invalid("Invalid chat id."));
            return RunAsync(accountId, async (userId, c) =>
            {
                await reader.PostEmptyAsync(userId, accountId, $"chats/{Uri.EscapeDataString(chatId)}/read", c);
                return NoContent();
            }, ct);
        }

        [HttpGet("{accountId:guid}/photos/{userId}")]
        public Task<IActionResult> Photo(Guid accountId, string userId, [FromQuery] int size = 64, CancellationToken ct = default)
        {
            if (!UserIdPattern.IsMatch(userId))
                return Task.FromResult(Invalid("Invalid user id."));
            return RunAsync(accountId, async (currentUserId, c) =>
            {
                var photo = await reader.GetPhotoAsync(currentUserId, accountId, userId, Math.Clamp(size, 16, 648), c);
                if (photo.Bytes is null)
                    return NotFound();
                Response.Headers.CacheControl = "private, max-age=3600";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                return File(photo.Bytes, photo.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? photo.ContentType : "image/jpeg");
            }, ct);
        }

        private IActionResult Invalid(string message) => BadRequest(new { code = "invalid_request", message });

        private Task<IActionResult> ReadAsync(Guid accountId, string path, CancellationToken ct) =>
            RunAsync(accountId, async (userId, c) => Ok(await reader.ReadAsync(userId, accountId, path, c)), ct);

        /// <summary>Checks the account is one of the current user's linked Microsoft accounts, runs the action and maps an expired sign-in to 409.</summary>
        private async Task<IActionResult> RunAsync(Guid accountId, Func<Guid, CancellationToken, Task<IActionResult>> action, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await linkedAccounts.GetAsync(userId, accountId, ct);
            if (account is null || account.Provider != LinkedProviders.Microsoft)
                return NotFound();

            try
            {
                return await action(userId, ct);
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                // 409, not 401: clients read 401 as their own Schuly session expiring and sign out.
                return Conflict(new { code = "microsoft_signin_expired", message = ex.Message });
            }
        }
    }

    public record SendChatMessageRequest(string? Text);
}
