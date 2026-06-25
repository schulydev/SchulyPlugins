using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Services;

namespace Schuly.Plugin.OdaOrg.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/plugins/odaorg/accounts")]
    public class AccountsController(IPluginUserContext userContext, OdaOrgDbContext db, OdaOrgSecretStore secretStore) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            // Credentials are vault-only ([NotMapped]) — report their presence from
            // the vault rather than exposing the username from the DB.
            var accounts = await db.Accounts
                .Where(a => a.ApplicationUserId == userId)
                .Select(a => new { a.Id, a.BaseUrl, a.DisplayName, a.SchoolUserId, a.AutoRefresh, a.CreatedAt, a.UpdatedAt })
                .ToListAsync();

            var result = accounts.Select(a => new
            {
                a.Id, a.BaseUrl, a.DisplayName, a.SchoolUserId, a.AutoRefresh,
                HasCredentials = secretStore.Has(a.Id),
                a.CreatedAt, a.UpdatedAt,
            });
            return Ok(result);
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
            secretStore.Remove(accountId); // drop the account's credentials from the vault
            return NoContent();
        }
    }
}
