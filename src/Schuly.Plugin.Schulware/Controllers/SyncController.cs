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
    [Route("api/plugins/schulware/accounts/{accountId:guid}/sync")]
    public class SyncController(
        IPluginUserContext userContext,
        SchulwareDbContext db,
        SchulwareSyncTask syncTask) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Status(Guid accountId)
        {
            var account = await ResolveAccountAsync(accountId);
            if (account is null) return NotFound();

            var syncState = await db.SyncStates.FirstOrDefaultAsync(s => s.AccountId == accountId);
            return Ok(new
            {
                account.Id, account.SchulnetzBaseUrl, account.DisplayName,
                HasMobileToken = account.MobileAccessToken is not null,
                HasWebSession = account.WebSessionId is not null,
                LastSync = syncState?.LastSyncAt,
                SyncStatus = syncState?.LastSyncStatus,
                SyncError = syncState?.LastSyncError,
            });
        }

        [HttpPost]
        public async Task<IActionResult> Run(
            Guid accountId, [FromServices] IServiceProvider services, CancellationToken ct)
        {
            var account = await ResolveAccountAsync(accountId);
            if (account is null) return NotFound();

            var result = await syncTask.SyncAccountAsync(accountId, services, ct);
            return Ok(new { result.LastSyncAt, result.LastSyncStatus, result.LastSyncError });
        }

        private async Task<SchulwareAccount?> ResolveAccountAsync(Guid accountId)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            return await db.Accounts.FirstOrDefaultAsync(
                a => a.Id == accountId && a.ApplicationUserId == userId);
        }
    }
}
