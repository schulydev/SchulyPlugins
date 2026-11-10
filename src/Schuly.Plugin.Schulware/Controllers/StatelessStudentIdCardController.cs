using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware.Controllers
{
    /// <summary>
    /// The Lernendenausweis with the caller's own Schulnetz token (<c>X-Plugin-Token</c>) and school URL
    /// (<c>X-Provider-Base-Url</c>). Unlike the other stateless routes this one requires a signed-in Schuly user:
    /// no new endpoint is reachable without authentication. Nothing is stored.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/plugins/schulware/stateless/student-id-card")]
    public class StatelessStudentIdCardController(IHttpClientFactory httpClientFactory, IConfiguration configuration, StudentIdCardService cards) : ControllerBase
    {
        [HttpGet]
        public async Task<IActionResult> Get(CancellationToken ct)
        {
            StudentIdCardHeaders.NoStore(Response);
            var token = Request.Headers["X-Plugin-Token"].ToString();
            var schulnetzBaseUrl = Request.Headers["X-Provider-Base-Url"].ToString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest(ApiError.InvalidRequest("Missing X-Plugin-Token header"));
            if (string.IsNullOrWhiteSpace(schulnetzBaseUrl))
                return BadRequest(ApiError.InvalidRequest("Missing X-Provider-Base-Url header"));
            if (!BaseUrlGuard.IsAllowed(schulnetzBaseUrl))
                return BadRequest(ApiError.InvalidRequest("Provider base URL is not allowed"));

            try
            {
                var client = SchulwareApiClientFactory.Create(httpClientFactory, SchulwareApiUrl.Required(configuration), schulnetzBaseUrl.TrimEnd('/'), token);
                StudentIdCardResponse card = await cards.FetchAsync(new StatelessStudentIdCardSource(client), true, ct);
                return Ok(card);
            }
            catch (StudentIdCardException ex)
            {
                return StatusCode(ex.StatusCode, new { code = ex.Code, message = ex.Message });
            }
        }
    }
}
