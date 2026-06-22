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
