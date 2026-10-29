using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>
    /// Stateless absence reporting for the app's private mode: the caller sends its own Schulnetz token
    /// (<c>X-Plugin-Token</c>) and school URL (<c>X-Provider-Base-Url</c>); nothing is stored or synced here.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/schulware/stateless/absences")]
    public class StatelessAbsenceReportController(IHttpClientFactory httpClientFactory, IConfiguration configuration) : ControllerBase
    {
        private string ProxyBaseUrl =>
            configuration["SchulwareApi:BaseUrl"] ?? "https://schlwr.pianonic.ch";

        [HttpGet("reported")]
        public Task<IActionResult> Reported(CancellationToken ct) =>
            RunAsync(async reporter => Ok(await reporter.ListReportedAsync(ct)));

        [HttpPost]
        public Task<IActionResult> Create([FromBody] AbsenceReportRequest request, CancellationToken ct) =>
            RunAsync(async reporter => Ok(await reporter.CreateAsync(request, ct)));

        [HttpDelete]
        public Task<IActionResult> Withdraw([FromQuery] string[] ids, CancellationToken ct) =>
            RunAsync(async reporter =>
            {
                await reporter.DeleteAsync(AbsenceReporter.ParseIds(ids), ct);
                return NoContent();
            });

        private async Task<IActionResult> RunAsync(Func<AbsenceReporter, Task<IActionResult>> operation)
        {
            var token = Request.Headers["X-Plugin-Token"].ToString();
            var schulnetzBaseUrl = Request.Headers["X-Provider-Base-Url"].ToString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(new { code = "invalid_request", message = "Missing X-Plugin-Token header" });
            if (string.IsNullOrWhiteSpace(schulnetzBaseUrl))
                return BadRequest(new { code = "invalid_request", message = "Missing X-Provider-Base-Url header" });
            if (!BaseUrlGuard.IsAllowed(schulnetzBaseUrl))
                return BadRequest(new { code = "invalid_request", message = "X-Provider-Base-Url is not an allowed target" });

            try
            {
                var http = SchulwareApiClientFactory.CreateHttpClient(httpClientFactory, ProxyBaseUrl, schulnetzBaseUrl.TrimEnd('/'), token);
                return await operation(new AbsenceReporter(new SchulnetzAbsenceApi(http, ProxyBaseUrl)));
            }
            catch (AbsenceReportException ex)
            {
                return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
            }
        }
    }
}
