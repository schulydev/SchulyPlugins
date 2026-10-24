using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>
    /// Private mode's Microsoft link: the same browser sign-in as <see cref="LinkController"/>, but nothing
    /// is stored. The device keeps the PKCE verifier and the Microsoft session cookies itself; <c>complete</c>
    /// only names the account so the app can show it in the Schulnetz account picker.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/microsoft/stateless/link")]
    public class StatelessLinkController(MicrosoftServiceClient service) : ControllerBase
    {
        public sealed record CompleteRequest(string Code, string CodeVerifier);

        [HttpPost("start")]
        public async Task<IActionResult> Start(CancellationToken ct)
        {
            var start = await service.StartBrowserLoginAsync(ct);
            return Ok(new { start.AuthorizeUrl, start.RedirectUri, start.CodeVerifier });
        }

        [HttpPost("complete")]
        public async Task<IActionResult> Complete([FromBody] CompleteRequest body, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(body.Code) || string.IsNullOrWhiteSpace(body.CodeVerifier))
                return BadRequest(new { message = "No authorization code." });

            var result = await service.CompleteBrowserLoginAsync(body.Code, body.CodeVerifier, ct);
            if (result.Status != "complete" || string.IsNullOrEmpty(result.Email))
                return Ok(new { status = "error", message = result.Message ?? "The Microsoft sign-in failed." });
            return Ok(new { status = "complete", result.Email, displayName = result.Name });
        }
    }
}
