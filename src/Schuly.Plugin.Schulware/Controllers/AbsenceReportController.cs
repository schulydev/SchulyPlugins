using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>Reports, lists and withdraws absences in Schulnetz for one of the caller's own accounts, then re-syncs it.</summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/accounts/{accountId:guid}/absences")]
    public class AbsenceReportController(IPluginUserContext userContext, SchulwareDbContext db, AccountSecretStore secretStore, TokenRefreshService refresh, IHttpClientFactory httpClientFactory, SchulwareSyncTask syncTask, IServiceProvider services, IConfiguration configuration, ILogger<AbsenceReportController> logger) : ControllerBase
    {
        [HttpGet("reported")]
        public Task<IActionResult> Reported(Guid accountId, CancellationToken ct) =>
            RunAsync(accountId, false, async (reporter, _) => Ok(await reporter.ListReportedAsync(ct)), ct);

        [HttpGet("reasons")]
        public Task<IActionResult> Reasons(Guid accountId, CancellationToken ct) =>
            RunAsync(accountId, false, async (reporter, account) => Ok(await reporter.ListReasonsAsync(AbsenceReasons.Configured(configuration, account.SchulnetzBaseUrl), logger, ct)), ct);

        [HttpPost]
        public Task<IActionResult> Create(Guid accountId, [FromBody] AbsenceReportRequest request, CancellationToken ct) =>
            RunAsync(accountId, true, async (reporter, _) => Ok(await reporter.CreateAsync(request, ct)), ct);

        [HttpDelete]
        public Task<IActionResult> Withdraw(Guid accountId, [FromQuery] string[] ids, CancellationToken ct) =>
            RunAsync(accountId, true, async (reporter, _) =>
            {
                await reporter.DeleteAsync(AbsenceReporter.ParseIds(ids), ct);
                return NoContent();
            }, ct);

        private async Task<IActionResult> RunAsync(Guid accountId, bool resync, Func<AbsenceReporter, SchulwareAccount, Task<IActionResult>> operation, CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId, ct);
            if (account is null) return NotFound();

            try
            {
                if (!secretStore.Hydrate(account) || account.MobileAccessToken is null)
                    throw AbsenceReportException.SignInExpired();
                if (account.MobileTokenExpiresAt.HasValue && account.MobileTokenExpiresAt < DateTime.UtcNow)
                    await RefreshAsync(account, ct);

                IActionResult result;
                try
                {
                    result = await operation(CreateReporter(account), account);
                }
                catch (AbsenceReportException ex) when (ex.Code == "signin_expired")
                {
                    await RefreshAsync(account, ct);
                    result = await operation(CreateReporter(account), account);
                }

                if (resync) await ResyncAsync(accountId, ct);
                return result;
            }
            catch (AbsenceReportException ex)
            {
                return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
            }
        }

        private async Task RefreshAsync(SchulwareAccount account, CancellationToken ct)
        {
            if (!await refresh.RefreshAsync(account, ct) || account.MobileAccessToken is null)
                throw AbsenceReportException.SignInExpired();
            secretStore.Save(account);
        }

        private AbsenceReporter CreateReporter(SchulwareAccount account)
        {
            var http = SchulwareApiClientFactory.CreateHttpClient(httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken);
            return new AbsenceReporter(new SchulnetzAbsenceApi(http, account.SchulwareApiBaseUrl));
        }

        /// <summary>Pulls the change into the Schuly absences list. The Schulnetz change already went through, so a failed sync must not fail the request.</summary>
        private async Task ResyncAsync(Guid accountId, CancellationToken ct)
        {
            try
            {
                await syncTask.SyncAccountAsync(accountId, services, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Absence sync after reporting failed for account {AccountId}", accountId);
            }
        }
    }
}
