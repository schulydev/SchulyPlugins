using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
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
    public class StatelessAuthController(IHttpClientFactory httpClientFactory, IConfiguration configuration, ILogger<StatelessAuthController> logger) : ControllerBase
    {
        private const string StatelessPath = "/api/plugins/schulware/stateless";

        private string BaseUrl =>
            configuration["SchulwareApi:BaseUrl"] ?? "https://schlwr.pianonic.ch";

        /// <summary>The descriptor's <c>LoginMethodPath</c>: a Microsoft-SSO school signs in with a linked Microsoft account.</summary>
        [HttpGet("login-method")]
        public async Task<IActionResult> LoginMethod([FromQuery] string baseUrl, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || !BaseUrlGuard.IsAllowed(baseUrl))
                return BadRequest(new { message = "baseUrl is not an allowed target" });

            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl, baseUrl.TrimEnd('/'));
                var result = await client.Api.Authenticate.LoginMethod.GetAsync(cancellationToken: ct);
                return Ok(result?.Method switch
                {
                    LoginMethodDto_method.Microsoft => new LoginMethodResult(LinkedProviders.Microsoft),
                    LoginMethodDto_method.Browser => new LoginMethodResult(null) { BrowserLoginPath = $"{StatelessPath}/browser/start" },
                    _ => new LoginMethodResult(null),
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not detect the Schulnetz login method; falling back to credentials");
                return Ok(new LoginMethodResult(null));
            }
        }

        [HttpPost("browser/start")]
        public async Task<IActionResult> BrowserStart([FromBody] StatelessBrowserStartRequest request, CancellationToken ct)
        {
            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return BadRequest(new { message = "baseUrl is not an allowed target" });

            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl);
            var start = await client.Api.Authenticate.Browser.Start.PostAsync(new BrowserLoginStartRequestDto { SchulnetzBaseUrl = request.BaseUrl.TrimEnd('/') }, cancellationToken: ct);
            if (start is null)
                return StatusCode(502, new { message = "Could not start the sign-in" });

            var context = BrowserSignIn.Serialize(new BrowserSignIn.Context(start.CodeVerifier!, start.DeviceId!, start.State!));
            return Ok(new BrowserLoginStart(start.AuthUrl!, start.CallbackUrl!, start.State!, context, $"{StatelessPath}/browser/complete"));
        }

        [HttpPost("browser/complete")]
        public async Task<IActionResult> BrowserComplete([FromBody] StatelessBrowserCompleteRequest request, CancellationToken ct)
        {
            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return Ok(new StatelessRefreshResponse(false, "baseUrl is not an allowed target", null, null, null, null, null, null));

            var context = BrowserSignIn.Parse(request.BrowserContext);
            var code = context is null ? null : BrowserSignIn.CodeFrom(request.BrowserCallbackUrl, context);
            if (context is null || code is null)
                return Ok(new StatelessRefreshResponse(false, "The sign-in didn't finish. Try again.", null, null, null, null, null, null));

            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl);
            var result = await client.Api.Authenticate.Browser.Complete.PostAsync(new BrowserLoginCompleteRequestDto { SchulnetzBaseUrl = request.BaseUrl.TrimEnd('/'), Code = code, CodeVerifier = context.CodeVerifier, DeviceId = context.DeviceId }, cancellationToken: ct);
            if (result is null || result.Success != true)
                return Ok(new StatelessRefreshResponse(false, result?.Message ?? "Sign-in failed", null, null, null, null, null, null));

            return Ok(new StatelessRefreshResponse(true, result.Message, result.AccessToken, result.RefreshToken, null, null, null, BrowserState(result.RefreshToken, context.DeviceId)));
        }

        private static JsonElement BrowserState(string? refreshToken, string deviceId) => JsonSerializer.SerializeToElement(new { refreshToken, deviceId });

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] StatelessLoginRequest request)
        {
            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return Ok(new StatelessRefreshResponse(false, "baseUrl is not an allowed target", null, null, null, null, null, null));

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

        [HttpPost("refresh")]
        public async Task<IActionResult> Refresh([FromBody] StatelessRefreshRequest request)
        {
            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return Ok(new StatelessRefreshResponse(false, "baseUrl is not an allowed target", null, null, null, null, null, null));

            var client = SchulwareApiClientFactory.Create(httpClientFactory, BaseUrl);
            if (request.ContextState.ValueKind == JsonValueKind.Object && request.ContextState.TryGetProperty("deviceId", out var deviceId) && request.ContextState.TryGetProperty("refreshToken", out var refreshToken))
            {
                var refreshed = await client.Api.Authenticate.Refresh.PostAsync(new MobileTokenRefreshRequestDto { SchulnetzBaseUrl = request.BaseUrl.TrimEnd('/'), RefreshToken = refreshToken.GetString(), DeviceId = deviceId.GetString() });
                return Ok(refreshed?.Success == true
                    ? new StatelessRefreshResponse(true, refreshed.Message, refreshed.AccessToken, refreshed.RefreshToken, null, null, null, BrowserState(refreshed.RefreshToken, deviceId.GetString()!))
                    : new StatelessRefreshResponse(false, refreshed?.Message ?? "Sign in again", null, null, null, null, null, null));
            }

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

            JsonElement? rotated = SessionCookies.ToElement(result.SessionCookies);

            return Ok(new StatelessRefreshResponse(
                true, result.Message,
                result.AccessToken, result.RefreshToken,
                result.SessionId, result.WebSessionUserId, result.WebSessionTransId,
                rotated));
        }
    }
}
