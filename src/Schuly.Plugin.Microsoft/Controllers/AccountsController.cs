using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Microsoft 365 data of the current user's linked Microsoft accounts (listing and unlinking live in core: <c>/api/me/linked-accounts</c>).</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts")]
    public class AccountsController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, MicrosoftServiceClient service) : ControllerBase
    {
        [HttpGet("{accountId:guid}/mail")]
        public Task<IActionResult> Mail(Guid accountId, [FromQuery] string folder = "inbox", [FromQuery] int top = 25, [FromQuery] bool unread = false, [FromQuery] string? search = null, CancellationToken ct = default) =>
            ReadAsync(accountId, $"mail?folder={Uri.EscapeDataString(folder)}&top={Math.Clamp(top, 1, 100)}&unread={unread.ToString().ToLowerInvariant()}" + (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search)}"), ct);

        [HttpGet("{accountId:guid}/calendar")]
        public Task<IActionResult> Calendar(Guid accountId, [FromQuery] int top = 25, CancellationToken ct = default) =>
            ReadAsync(accountId, $"calendar?top={Math.Clamp(top, 1, 100)}", ct);

        [HttpGet("{accountId:guid}/files")]
        public Task<IActionResult> Files(Guid accountId, [FromQuery] string path = "root", [FromQuery] int top = 100, CancellationToken ct = default) =>
            ReadAsync(accountId, $"files?path={Uri.EscapeDataString(path)}&top={Math.Clamp(top, 1, 200)}", ct);

        private async Task<IActionResult> ReadAsync(Guid accountId, string path, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await linkedAccounts.GetAsync(userId, accountId, ct);
            if (account is null || account.Provider != LinkedProviders.Microsoft)
                return NotFound();

            var token = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct);
            if (token is null)
                return Unauthorized(new { message = "This Microsoft account has no stored sign-in; link it again." });

            try
            {
                var read = await service.ReadAsync(path, token, ct);
                if (read.RefreshToken != token)
                    await linkedAccounts.SetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, read.RefreshToken, ct);
                return Ok(read.Data);
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
        }
    }
}
