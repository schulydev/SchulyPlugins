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
    /// Links a Microsoft account with the device-code flow: <c>start</c> returns a short code to enter on
    /// Microsoft's own sign-in page, then the app polls <c>poll</c> until the student finishes.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/link")]
    public class LinkController(IPluginUserContext userContext, MicrosoftDbContext db, MicrosoftTokenStore tokens, MicrosoftServiceClient service) : ControllerBase
    {
        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var start = await service.StartDeviceLoginAsync(ct);
            tokens.SavePending(userId, new MicrosoftTokenStore.PendingLink(start.DeviceCode, DateTime.UtcNow.AddSeconds(start.ExpiresIn)));
            return Ok(new { start.UserCode, start.VerificationUri, start.ExpiresIn, start.Interval, start.Message });
        }

        [HttpPost("poll")]
        public async Task<IActionResult> Poll(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var pending = tokens.GetPending(userId);
            if (pending is null)
                return NotFound(new { message = "No Microsoft sign-in in progress; start one first." });
            if (pending.ExpiresAt < DateTime.UtcNow)
            {
                tokens.RemovePending(userId);
                return Ok(new { status = "expired", message = "The code expired; start the sign-in again." });
            }

            var poll = await service.PollDeviceLoginAsync(pending.DeviceCode, ct);
            if (poll.Status == "pending")
                return Ok(new { status = "pending" });
            tokens.RemovePending(userId);
            if (poll.Status != "complete" || string.IsNullOrEmpty(poll.RefreshToken) || string.IsNullOrEmpty(poll.Email))
                return Ok(new { status = "error", message = poll.Message ?? "The Microsoft sign-in failed." });

            var account = await db.Accounts.FirstOrDefaultAsync(a => a.ApplicationUserId == userId && a.Email == poll.Email, ct);
            if (account is null)
            {
                account = new MicrosoftAccount { ApplicationUserId = userId, Email = poll.Email };
                db.Accounts.Add(account);
            }
            account.MicrosoftUserId = poll.MicrosoftUserId;
            account.DisplayName = poll.Name;
            account.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            tokens.SaveToken(account.Id, poll.RefreshToken);

            return Ok(new { status = "complete", accountId = account.Id, account.Email, account.DisplayName });
        }
    }
}
