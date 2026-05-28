using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;

namespace Schuly.Plugin.OdaOrg.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/plugins/odaorg/accounts")]
    public class AccountsController(IPluginUserContext userContext, OdaOrgDbContext db) : ControllerBase
    {
        public record ConnectOdaOrgRequest(string Username, string Password, string? BaseUrl, string? DisplayName);

        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var accounts = await db.Accounts
                .Where(a => a.ApplicationUserId == userId)
                .Select(a => new { a.Id, a.BaseUrl, a.Username, a.DisplayName, a.SchoolUserId, a.CreatedAt, a.UpdatedAt })
                .ToListAsync();
            return Ok(accounts);
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] ConnectOdaOrgRequest request)
        {
            if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
                return BadRequest("Username and password are required");

            var userId = await userContext.GetCurrentUserIdAsync();
            var baseUrl = string.IsNullOrWhiteSpace(request.BaseUrl)
                ? "https://odaorg.ict-bbag.ch" : request.BaseUrl.TrimEnd('/');

            if (await db.Accounts.AnyAsync(a => a.ApplicationUserId == userId && a.BaseUrl == baseUrl))
                return BadRequest("Account for this OdaOrg instance already connected");

            var account = new OdaOrgAccount
            {
                ApplicationUserId = userId,
                BaseUrl = baseUrl,
                Username = request.Username,
                Password = request.Password,
                DisplayName = request.DisplayName,
            };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            return Ok(new { account.Id, Message = "Account created. It will sync on the next run, or trigger it now." });
        }

        [HttpDelete("{accountId:guid}")]
        public async Task<IActionResult> Delete(Guid accountId)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
            if (account is null) return NotFound();

            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
            if (state is not null) db.SyncStates.Remove(state);
            db.Accounts.Remove(account);
            await db.SaveChangesAsync();
            return NoContent();
        }
    }
}
