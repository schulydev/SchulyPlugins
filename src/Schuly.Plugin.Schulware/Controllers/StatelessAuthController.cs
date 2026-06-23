using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>
    /// Stateless, account-free proxy to SchulwareAPI for the app's private mode.
    /// Persists nothing: the caller owns the tokens and context_state and passes
    /// them back in on each request. Anonymous — no Schuly account / OIDC needed.
    /// The SchulwareAPI base URL is resolved server-side (same default as account
    /// creation), so callers can't point it at an arbitrary proxy.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/schulware/stateless")]
    public class StatelessAuthController(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration) : ControllerBase
    {
        private string BaseUrl =>
            configuration["Schulware:DefaultApiBaseUrl"] ?? "https://schlwr.pianonic.ch";

        /// <summary>
        /// Headless credential login (email + password [+ TOTP]) via SchulwareAPI's
        /// ms-entrance flow — no browser, no WebView. Hands back tokens, web session
        /// and context_state for the caller to persist. The private-mode replacement
        /// for the interactive OAuth (authorize-url + callback) pair.
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] StatelessLoginRequest request)
        {
            var client = SchulwareApiClientFactory.Create(
                httpClientFactory, BaseUrl, request.BaseUrl);

            var result = await client.Api.Authenticate.Login.PostAsync(new LoginRequestDto
            {
                SchulnetzBaseUrl = request.BaseUrl,
                Email = request.Email,
                Password = request.Password,
                TotpSecret = string.IsNullOrWhiteSpace(request.TotpSecret) ? null : request.TotpSecret,
            });

            if (result is null || result.Success != true)
                return Ok(new StatelessRefreshResponse(
                    false, result?.Message ?? "Login failed",
                    null, null, null, null, null, null));

            // The opaque blob the caller persists is now the session_cookies jar.
            JsonElement? ctx = SessionCookies.ToElement(result.SessionCookies);

            return Ok(new StatelessRefreshResponse(
                true, result.Message,
                result.AccessToken, result.RefreshToken,
                result.SessionId, result.WebSessionUserId, result.WebSessionTransId,
                ctx));
        }

        /// <summary>Passwordless refresh: replay the caller's stored session cookies via SchulwareAPI's /login.</summary>
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] StatelessRefreshRequest request)
        {
            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl);
            var result = await client.Api.Authenticate.Login.PostAsync(
                new LoginRequestDto
                {
                    SchulnetzBaseUrl = request.BaseUrl,
                    UserAgent = request.UserAgent,
                    SessionCookies = SessionCookies.FromElement(request.ContextState),
                });

            if (result is null || result.Success != true)
                return Ok(new StatelessRefreshResponse(
                    false, result?.Message ?? "Refresh failed",
                    null, null, null, null, null, null));

            // Hand back the rotated session_cookies for the caller to persist.
            JsonElement? rotated = SessionCookies.ToElement(result.SessionCookies);

            return Ok(new StatelessRefreshResponse(
                true, result.Message,
                result.AccessToken, result.RefreshToken,
                result.SessionId, result.WebSessionUserId, result.WebSessionTransId,
                rotated));
        }
    }
}
