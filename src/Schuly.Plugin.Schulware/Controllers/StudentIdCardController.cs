using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>
    /// The official Schulnetz Lernendenausweis of one of the caller's own accounts. Fetched live on every call and never
    /// stored: no database row, no file, no log line carries the card.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/accounts/{accountId:guid}/student-id-card")]
    public class StudentIdCardController(IPluginUserContext userContext, SchulwareDbContext db, AccountSecretStore secretStore, TokenRefreshService refresh, IHttpClientFactory httpClientFactory, StudentIdCardService cards, SchulnetzAccountPictures pictures, ILogger<StudentIdCardController> logger) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get(Guid accountId, CancellationToken ct)
        {
            StudentIdCardHeaders.NoStore(Response);
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == accountId && a.ApplicationUserId == userId, ct);
            if (account is null) return NotFound();

            var refreshed = false;
            var sources = new List<AccountStudentIdCardSource>();
            try
            {
                if (!secretStore.Hydrate(account)) throw StudentIdCardException.SignInExpired();
                var mobile = account.MobileAccessToken is not null && account.MobileApiAvailable != false;
                if (mobile && account.MobileTokenExpiresAt.HasValue && account.MobileTokenExpiresAt < DateTime.UtcNow)
                    refreshed = await RefreshAsync(account, ct);

                StudentIdCardResponse card;
                try
                {
                    card = await cards.FetchAsync(CreateSource(account, sources), mobile, ct);
                }
                catch (StudentIdCardException ex) when (ex.Code == "signin_expired" && mobile)
                {
                    refreshed |= await RefreshAsync(account, ct);
                    card = await cards.FetchAsync(CreateSource(account, sources), mobile, ct);
                }
                // Only the photo is kept, as the account's picture; the card itself still never is.
                await pictures.StoreFromCardAsync(account, card.Html, ct);
                return Ok(card);
            }
            catch (StudentIdCardException ex)
            {
                if (ex.Code != "not_offered") logger.LogInformation("Student ID card for account {AccountId} failed: {Code}", accountId, ex.Code);
                return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
            }
            finally
            {
                // Only the account's own sign-in secrets are written back, never the card.
                if (refreshed || sources.Any(s => s.SessionChanged)) secretStore.Save(account);
            }
        }

        private async Task<bool> RefreshAsync(SchulwareAccount account, CancellationToken ct)
        {
            if (!await refresh.RefreshAsync(account, ct) || account.MobileAccessToken is null)
                throw StudentIdCardException.SignInExpired();
            return true;
        }

        private AccountStudentIdCardSource CreateSource(SchulwareAccount account, List<AccountStudentIdCardSource> sources)
        {
            var source = new AccountStudentIdCardSource(SchulwareApiClientFactory.Create(httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl, account.MobileAccessToken), account, refresh);
            sources.Add(source);
            return source;
        }
    }
}
