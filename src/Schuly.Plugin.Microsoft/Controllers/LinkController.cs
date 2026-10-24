using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Data;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>
    /// Links a Microsoft account with the browser sign-in: <c>start</c> returns the Microsoft authorize
    /// URL the app opens in a WebView; the app captures the authorization code from the redirect and
    /// posts it to <c>complete</c>. The student signs in on Microsoft's own page; no password reaches Schuly.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/link")]
    public class LinkController(IPluginUserContext userContext, MicrosoftDbContext db, MicrosoftTokenStore tokens, MicrosoftServiceClient service) : ControllerBase
    {
        public sealed record CompleteRequest(string Code);

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var start = await service.StartBrowserLoginAsync(ct);
            tokens.SavePending(userId, new MicrosoftTokenStore.PendingLink(start.CodeVerifier, DateTime.UtcNow.AddMinutes(10)));
            return Ok(new { start.AuthorizeUrl, start.RedirectUri });
        }

        [HttpPost("complete")]
        public async Task<IActionResult> Complete([FromBody] CompleteRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Code))
                return BadRequest(new { message = "No authorization code." });

            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var pending = tokens.GetPending(userId);
            if (pending is null || pending.ExpiresAt < DateTime.UtcNow)
            {
                tokens.RemovePending(userId);
                return Ok(new { status = "expired", message = "The sign-in took too long; start it again." });
            }

            var result = await service.CompleteBrowserLoginAsync(body.Code, pending.CodeVerifier, ct);
            tokens.RemovePending(userId);
            if (result.Status != "complete" || string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.Email))
                return Ok(new { status = "error", message = result.Message ?? "The Microsoft sign-in failed." });

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.ApplicationUserId == userId && a.Email == result.Email, ct);
            if (account is null)
            {
                account = new MicrosoftAccount { ApplicationUserId = userId, Email = result.Email };
                db.Accounts.Add(account);
            }
            account.MicrosoftUserId = result.MicrosoftUserId;
            account.DisplayName = result.Name;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            tokens.SaveToken(account.Id, result.RefreshToken);

            return Ok(new { status = "complete", accountId = account.Id, account.Email, account.DisplayName });
        }
    }
}
