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

        /// <summary>Begin OAuth: fetch the Schulnetz authorize URL + PKCE verifier.</summary>
        [HttpGet("authorize-url")]
        public async Task<IActionResult> GetAuthorizeUrl([FromQuery] string schulnetzBaseUrl)
        {
            if (string.IsNullOrWhiteSpace(schulnetzBaseUrl))
                return BadRequest("schulnetzBaseUrl is required");

            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl, schulnetzBaseUrl);
            var result = await client.Api.Authenticate.Oauth.Mobile.Url.GetAsync();
            if (result is null) return BadRequest("Failed to get authorize URL");

            return Ok(new StatelessAuthorizeUrlResponse(result.AuthorizationUrl, result.CodeVerifier));
        }

        /// <summary>Exchange the OAuth code for tokens and hand them back. Stores nothing.</summary>
        [HttpPost("oauth/callback")]
        public async Task<IActionResult> Callback([FromBody] StatelessOAuthCallbackRequest request)
        {
            var client = SchulwareApiClientFactory.Create(
                httpClientFactory, BaseUrl, request.SchulnetzBaseUrl);

            var tokens = await client.Api.Authenticate.Oauth.Mobile.Callback.PostAsync(
                new MobileCallbackRequestDto
                {
                    Code = request.Code,
                    CodeVerifier = request.CodeVerifier,
                    State = request.State,
                });
            if (tokens is null) return BadRequest("Failed to exchange OAuth code");

            return Ok(new StatelessTokenResponse(tokens.AccessToken, tokens.RefreshToken));
        }

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
                httpClientFactory, BaseUrl, request.SchulnetzBaseUrl);

            var result = await client.Api.Authenticate.Login.PostAsync(new LoginRequestDto
            {
                SchulnetzBaseUrl = request.SchulnetzBaseUrl,
                Email = request.Email,
                Password = request.Password,
                TotpSecret = string.IsNullOrWhiteSpace(request.TotpSecret) ? null : request.TotpSecret,
            });

            if (result is null || result.Success != true)
                return Ok(new StatelessRefreshResponse(
                    false, result?.Message ?? "Login failed",
                    null, null, null, null, null, null));

            JsonElement? ctx = null;
            if (result.ContextState?.AdditionalData is { Count: > 0 } bag)
                ctx = JsonSerializer.Deserialize<JsonElement>(JsonBag.Serialize(bag));

            return Ok(new StatelessRefreshResponse(
                true, result.Message,
                result.AccessToken, result.RefreshToken,
                result.SessionId, result.WebSessionUserId, result.WebSessionTransId,
                ctx));
        }

        /// <summary>Passwordless refresh: replay the caller's context_state via SchulwareAPI.</summary>
        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] StatelessRefreshRequest request)
        {
            var contextState = new RefreshTokenRequestDto_context_state
            {
                AdditionalData = JsonBag.ParseObject(request.ContextState.GetRawText()),
            };

            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl);
            var result = await client.Api.Authenticate.Refresh.PostAsync(
                new RefreshTokenRequestDto
                {
                    SchulnetzBaseUrl = request.SchulnetzBaseUrl,
                    UserAgent = request.UserAgent,
                    ContextState = contextState,
                });

            if (result is null || result.Success != true)
                return Ok(new StatelessRefreshResponse(
                    false, result?.Message ?? "Refresh failed",
                    null, null, null, null, null, null));

            // Return the rotated context_state as a JSON object for the caller to persist.
            // JsonBag.Serialize lowers Kiota's UntypedNode tree to plain JSON first.
            JsonElement? rotated = null;
            if (result.ContextState?.AdditionalData is { Count: > 0 } bag)
                rotated = JsonSerializer.Deserialize<JsonElement>(JsonBag.Serialize(bag));

            return Ok(new StatelessRefreshResponse(
                true, result.Message,
                result.AccessToken, result.RefreshToken,
                result.SessionId, result.WebSessionUserId, result.WebSessionTransId,
                rotated));
        }
    }
}
