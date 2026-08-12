using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/accounts")]
    public class AccountsController(IPluginUserContext userContext, SchulwareDbContext db, AccountSecretStore secretStore) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> List()
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            // Secrets are vault-only ([NotMapped]), so we can't project them in SQL —
            // fetch the metadata and report secret presence from the vault in memory.
            var accounts = await db.Accounts
                .Where(a => a.ApplicationUserId == userId)
                .Select(a => new
                {
                    a.Id, a.SchulnetzBaseUrl, a.DisplayName, a.SchulnetzStudentId,
                    a.SchoolUserId, a.AutoRefresh, a.MobileTokenExpiresAt, a.CreatedAt,
                })
                .ToListAsync();

            var result = accounts.Select(a => new
            {
                a.Id, a.SchulnetzBaseUrl, a.DisplayName, a.SchulnetzStudentId,
                a.SchoolUserId, a.AutoRefresh,
                HasSecrets = secretStore.Has(a.Id),
                a.MobileTokenExpiresAt, a.CreatedAt,
            });
            return Ok(result);
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
            secretStore.Remove(accountId);
            return NoContent();
        }
    }
}
