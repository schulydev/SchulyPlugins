using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/accounts/{accountId:guid}/auth/oauth")]
    public class OAuthController(
        IPluginUserContext userContext,
        SchulwareDbContext db,
        IHttpClientFactory httpClientFactory,
        OAuthCallbackService callbackService) : ControllerBase
    {
        [HttpGet("url")]
        public async Task<IActionResult> GetAuthorizeUrl(Guid accountId)
        {
            var account = await ResolveAccountAsync(accountId);
            if (account is null) return NotFound();

            var client = SchulwareApiClientFactory.Create(
                httpClientFactory, account.SchulwareApiBaseUrl, account.SchulnetzBaseUrl);
            var result = await client.Api.Authenticate.Oauth.Mobile.Url.GetAsync();
            return Ok(result);
        }

        [HttpPost("callback")]
        public async Task<IActionResult> Callback(
            Guid accountId, [FromBody] OAuthCallbackRequest request, [FromServices] IServiceProvider services)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            var account = await db.Accounts.FirstOrDefaultAsync(
                a => a.Id == accountId && a.ApplicationUserId == userId);
            if (account is null) return NotFound();

            var result = await callbackService.HandleAsync(account, userId, request, services);
            if (!result.Success) return BadRequest(result.Error);

            return Ok(new
            {
                Success = true,
                Message = "Authenticated and session captured",
                InitialSyncStatus = result.InitialSyncStatus,
                InitialSyncError = result.InitialSyncError,
            });
        }

        private async Task<SchulwareAccount?> ResolveAccountAsync(Guid accountId)
        {
            var userId = await userContext.GetCurrentUserIdAsync();
            return await db.Accounts.FirstOrDefaultAsync(
                a => a.Id == accountId && a.ApplicationUserId == userId);
        }
    }
}
