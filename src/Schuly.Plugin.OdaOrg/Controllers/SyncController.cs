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
    [Route("api/plugins/odaorg/accounts/{accountId:guid}/sync")]
    public class SyncController(IPluginUserContext userContext, OdaOrgDbContext db, OdaOrgSyncTask syncTask) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Status(Guid accountId)
        {
            var account = await ResolveAsync(accountId);
            if (account is null) return NotFound();

            var state = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
            return Ok(new
            {
                account.Id, account.BaseUrl, account.DisplayName, account.SchoolUserId,
                LastSync = state?.LastSyncAt,
                SyncStatus = state?.LastSyncStatus,
                SyncError = state?.LastSyncError,
            });
        }

        [HttpPost]
        public async Task<IActionResult> Run(Guid accountId, [FromServices] IServiceProvider services, CancellationToken ct)
        {
            var account = await ResolveAsync(accountId);
            if (account is null) return NotFound();

            var state = await syncTask.SyncAccountAsync(accountId, services, ct);
            return Ok(new { state.LastSyncAt, state.LastSyncStatus, state.LastSyncError });
        }

        private async Task<OdaOrgAccount?> ResolveAsync(Guid accountId)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            return await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId);
        }
    }
}
