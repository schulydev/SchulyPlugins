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
    /// Kept for app versions that predate the link providers.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/microsoft/link")]
    public class LinkController(IPluginUserContext userContext, MicrosoftAccountLinker linker, PendingLinkStore pending, MicrosoftServiceClient service) : ControllerBase
    {
        public sealed record CompleteRequest(string Code, List<MicrosoftSession.Cookie>? SessionCookies, string? SessionId = null);

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            var userId = await userContext.GetCurrentUserIdAsync(ct);
            var start = await service.StartBrowserLoginAsync(ct);
            var sessionId = pending.Save(userId, new PendingLinkStore.PendingLink(start.CodeVerifier, DateTime.UtcNow.AddMinutes(10)));
            return Ok(new { start.AuthorizeUrl, start.RedirectUri, sessionId });
        }

        [HttpPost("complete")]
        public async Task<IActionResult> Complete([FromBody] CompleteRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Code))
                return BadRequest(new { message = "No authorization code." });

            var userId = await userContext.GetCurrentUserIdAsync(ct);

            // A repeated completion (retry, double submit) with a code that already linked an account answers with that account.
            if (pending.CompletedAccount(userId, body.Code) is { } doneId)
                return Ok(new { status = "complete", accountId = doneId });

            var found = pending.Get(userId, body.SessionId);
            if (found is null || found.Value.Link.ExpiresAt < DateTime.UtcNow)
            {
                if (found is not null) pending.Remove(userId, found.Value.SessionId);
                return Ok(new { status = "expired", message = "The sign-in took too long; start it again." });
            }

            Infrastructure.SignInResult result;
            try
            {
                result = await service.CompleteBrowserLoginAsync(body.Code, found.Value.Link.CodeVerifier, ct);
            }
            catch (HttpRequestException)
            {
                // The pending session stays so the app can retry.
                return Ok(new { status = "error", message = "Microsoft could not be reached. Try again." });
            }

            if (result.Status != "complete" || string.IsNullOrEmpty(result.RefreshToken) || string.IsNullOrEmpty(result.Email))
                return Ok(new { status = "error", message = result.Message ?? "The Microsoft sign-in failed." });

            var account = await linker.SaveAsync(userId, result.MicrosoftUserId, result.Email, result.Name, result.RefreshToken, MicrosoftSession.ToStored(body.SessionCookies), ct);
            pending.RememberCompleted(userId, body.Code, account.Id);
            pending.Remove(userId, found.Value.SessionId);

            return Ok(new { status = "complete", accountId = account.Id, account.Email, account.DisplayName });
        }
    }
}
