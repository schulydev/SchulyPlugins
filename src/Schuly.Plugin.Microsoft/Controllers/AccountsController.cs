using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Data;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>The current user's linked Microsoft accounts and their Microsoft 365 data.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/accounts")]
    public class AccountsController(IPluginUserContext userContext, MicrosoftDbContext db, MicrosoftTokenStore tokens, MicrosoftServiceClient service) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> List(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var accounts = await db.Accounts
                .Where(a => a.ApplicationUserId == userId)
                .OrderBy(a => a.Email)
                .Select(a => new { a.Id, a.Email, a.DisplayName, a.CreatedAt })
                .ToListAsync(ct);
            return Ok(accounts);
        }

        [HttpDelete("{accountId:guid}")]
        public async Task<IActionResult> Unlink(Guid accountId, CancellationToken ct)
        {
            var account = await OwnedAsync(accountId, ct);
            if (account is null) return NotFound();
            db.Accounts.Remove(account);
            await db.SaveChangesAsync(ct);
            tokens.RemoveToken(accountId);
            return NoContent();
        }

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
            var account = await OwnedAsync(accountId, ct);
            if (account is null) return NotFound();
            var token = tokens.GetToken(accountId);
            if (token is null)
                return Unauthorized(new { message = "This Microsoft account has no stored sign-in; link it again." });

            try
            {
                var read = await service.ReadAsync(path, token, ct);
                if (read.RefreshToken != token)
                    tokens.SaveToken(accountId, read.RefreshToken);
                return Ok(read.Data);
            }
            catch (MicrosoftSignInExpiredException ex)
            {
                return Unauthorized(new { message = ex.Message });
            }
        }

        private async Task<MicrosoftAccount?> OwnedAsync(Guid accountId, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            return await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId, ct);
        }
    }
}
