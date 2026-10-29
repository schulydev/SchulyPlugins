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
        [HttpGet("{accountId:guid}/mail")]
        public Task<IActionResult> Mail(Guid accountId, [FromQuery] string folder = "inbox", [FromQuery] int top = 25, [FromQuery] bool unread = false, [FromQuery] string? search = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"mail?folder={Uri.EscapeDataString(folder)}&top={Math.Clamp(top, 1, 100)}&unread={unread.ToString().ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"), ct);

        [HttpGet("{accountId:guid}/calendar")]
        public Task<IActionResult> Calendar(Guid accountId, [FromQuery] int top = 25, [FromQuery] DateTimeOffset? start = null, [FromQuery] DateTimeOffset? end = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"calendar?top={Math.Clamp(top, 1, 500)}" + (start is { } s && end is { } e ? $"&start={Uri.EscapeDataString(s.UtcDateTime.ToString("o"))}&end={Uri.EscapeDataString(e.UtcDateTime.ToString("o"))}" : ""), ct);

        [HttpGet("{accountId:guid}/files")]
        public Task<IActionResult> Files(Guid accountId, [FromQuery] string path = "root", [FromQuery] int top = 100, CancellationToken ct = default) =>
            ReadAsync(accountId, $"files?path={Uri.EscapeDataString(path)}&top={Math.Clamp(top, 1, 200)}", ct);

        [HttpGet("{accountId:guid}/chats")]
        public Task<IActionResult> Chats(Guid accountId, [FromQuery] int top = 25, CancellationToken ct = default) =>
            ReadAsync(accountId, $"chats?top={Math.Clamp(top, 1, 50)}", ct);

        [HttpGet("{accountId:guid}/notebooks")]
        public Task<IActionResult> Notebooks(Guid accountId, CancellationToken ct = default) =>
            ReadAsync(accountId, "notebooks", ct);

        private async Task<IActionResult> ReadAsync(Guid accountId, string path, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await linkedAccounts.GetAsync(userId, accountId, ct);
            if (account is null || account.Provider != LinkedProviders.Microsoft)
                return NotFound();

            try
            {
                return Ok(await reader.ReadAsync(userId, accountId, path, ct));
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                // 409, not 401: clients read 401 as their own Schuly session expiring and sign out.
                return Conflict(new { code = "microsoft_signin_expired", message = ex.Message });
            }
        }
    }
}
