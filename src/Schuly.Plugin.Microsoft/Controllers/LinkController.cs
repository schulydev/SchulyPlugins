using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>
    /// Links a Microsoft account with the browser sign-in: <c>start</c> returns the Microsoft authorize
    /// URL the app opens in a WebView; the app captures the authorization code from the redirect and
    /// posts it to <c>complete</c> together with the Microsoft sign-in session cookies. The student signs
    /// in on Microsoft's own page; no password reaches Schuly. The result is a Schuly linked account.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/link")]
    public class LinkController(IPluginUserContext userContext, ILinkedAccounts linkedAccounts, PendingLinkStore pending, MicrosoftServiceClient service) : ControllerBase
    {
        public sealed record CompleteRequest(string Code, List<MicrosoftSession.Cookie>? SessionCookies);

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var start = await service.StartBrowserLoginAsync(ct);
            pending.Save(userId, new PendingLinkStore.PendingLink(start.CodeVerifier, DateTime.UtcNow.AddMinutes(10)));
            return Ok(new { start.AuthorizeUrl, start.RedirectUri });
        }

        [HttpPost("complete")]
        public async Task<IActionResult> Complete([FromBody] CompleteRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Code))
                return BadRequest(new { message = "No authorization code." });

            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var link = pending.Get(userId);
            if (link is null || link.ExpiresAt < DateTime.UtcNow)
            {
                pending.Remove(userId);
                return Ok(new { status = "expired", message = "The sign-in took too long; start it again." });
            }

            var result = await service.CompleteBrowserLoginAsync(body.Code, link.CodeVerifier, ct);
            pending.Remove(userId);
            if (result.Status != "complete" || string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.Email))
                return Ok(new { status = "error", message = result.Message ?? "The Microsoft sign-in failed." });

            var account = await linkedAccounts.SaveAsync(userId, new LinkedAccountRecord(LinkedProviders.Microsoft, result.MicrosoftUserId ?? result.Email, result.Email, result.Name), ct);
            await linkedAccounts.SetSecretAsync(userId, account.Id, LinkedSecrets.RefreshToken, result.RefreshToken, ct);
            if (MicrosoftSession.ToStored(body.SessionCookies) is { } session)
                await linkedAccounts.SetSecretAsync(userId, account.Id, LinkedSecrets.SessionCookies, session, ct);

            return Ok(new { status = "complete", accountId = account.Id, account.Email, account.DisplayName });
        }
    }
}
