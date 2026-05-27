using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;

namespace Schuly.Plugin.Schulware.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/accounts")]
    public class AccountsController(IPluginUserContext userContext, SchulwareDbContext db) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var accounts = await db.Accounts
                .Where(a => a.ApplicationUserId == userId)
                .Select(a => new
                {
                    a.Id, a.SchulnetzBaseUrl, a.DisplayName, a.SchulnetzStudentId,
                    a.SchoolUserId,
                    HasMobileToken = a.MobileAccessToken != null,
                    HasWebSession = a.WebSessionId != null,
                    a.MobileTokenExpiresAt, a.CreatedAt,
                })
                .ToListAsync();
            return Ok(accounts);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] ConnectAccountRequest request)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var exists = await db.Accounts.AnyAsync(a =>
                a.ApplicationUserId == userId && a.SchulnetzBaseUrl == request.SchulnetzBaseUrl);
            if (exists)
                return BadRequest("Account for this Schulnetz instance already connected");

            var account = new SchulwareAccount
            {
                ApplicationUserId = userId,
                SchulnetzBaseUrl = request.SchulnetzBaseUrl,
                SchulwareApiBaseUrl = request.SchulwareApiBaseUrl ?? "https://schlwr.pianonic.ch",
                DisplayName = request.DisplayName,
                SchoolUserId = request.SchoolUserId,
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return Ok(new { account.Id, Message = "Account created. Authenticate next." });
        }

        [HttpDelete("{accountId:guid}")]
        public async Task<IActionResult> Delete(Guid accountId)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var account = await db.Accounts.FirstOrDefaultAsync(
                a => a.Id == accountId && a.ApplicationUserId == userId);
            if (account is null) return NotFound();

            var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
            if (syncState is not null) db.SyncStates.Remove(syncState);

            db.Accounts.Remove(account);
            await db.SaveChangesAsync();
            return NoContent();
        }
    }
}
