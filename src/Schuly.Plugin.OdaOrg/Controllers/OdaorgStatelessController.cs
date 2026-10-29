using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/odaorg/stateless")]
    public class OdaorgStatelessController(OdaOrgScraper scraper, ILogger<OdaorgStatelessController> logger) : ControllerBase
    {
        [HttpPost("data")]
        [ProducesResponseType(typeof(OdaorgStatelessData), StatusCodes.Status200OK)]
        public async Task<IActionResult> Data([FromBody] OdaorgStatelessRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Username)
                || string.IsNullOrWhiteSpace(request.Password)
                || string.IsNullOrWhiteSpace(request.BaseUrl))
                return BadRequest(ApiError.InvalidRequest("username, password and baseUrl are required"));

            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return BadRequest(ApiError.InvalidRequest("baseUrl is not an allowed target"));

            OdaScrape? scrape;
            try
            {
                scrape = await scraper.ScrapeAsync(request.BaseUrl, request.Username, request.Password, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "OdaOrg stateless scrape failed");
                return StatusCode(StatusCodes.Status502BadGateway, ApiError.Unreachable());
            }
            if (scrape is null)
                return Unauthorized(new { code = "login_failed", message = ApiError.LoginFailedMessage });

            return Ok(Map(scrape));
        }

        private static OdaorgStatelessData Map(OdaScrape scrape)
        {
            var exams = new List<OdaorgStatelessExam>();
            var grades = new List<OdaorgStatelessGrade>();
            for (var i = 0; i < scrape.Grades.Count; i++)
            {
                var g = scrape.Grades[i];
                var examId = $"oda-exam-{i}";
                exams.Add(new OdaorgStatelessExam(examId, g.ModuleName, g.ModuleName));
                grades.Add(new OdaorgStatelessGrade(
                    $"oda-grade-{i}", examId, g.ModuleName, (double)g.FinalGrade));
            }

            var agenda = scrape.CourseDays.Select((c, i) => new OdaorgStatelessAgendaEvent(
                $"oda-day-{i}",
                string.IsNullOrWhiteSpace(c.Topic) ? c.Course : c.Topic!,
                c.Date.ToString("o", CultureInfo.InvariantCulture),
                c.EndDate?.ToString("o", CultureInfo.InvariantCulture),
                c.Room,
                c.Instructor)).ToList();

            var p = scrape.Profile;
            var userInfo = p is null
                ? null
                : new OdaorgStatelessUserInfo(
                    p.FirstName, p.LastName, p.Email,
                    p.Birthday?.ToString("o", CultureInfo.InvariantCulture));

            return new OdaorgStatelessData(userInfo, grades, exams, agenda);
        }
    }

    public record OdaorgStatelessRequest(string BaseUrl, string Username, string Password);

    public record OdaorgStatelessData(OdaorgStatelessUserInfo? UserInfo, List<OdaorgStatelessGrade> Grades, List<OdaorgStatelessExam> Exams, List<OdaorgStatelessAgendaEvent> Agenda);

    public record OdaorgStatelessUserInfo(string? FirstName, string? LastName, string? Email, string? Birthday);

    public record OdaorgStatelessGrade(string? Id, string? ExamId, string? Subject, double? Score);

    public record OdaorgStatelessExam(string? Id, string? Name, string? Subject);

    public record OdaorgStatelessAgendaEvent(string? Id, string? Title, string? StartDate, string? EndDate, string? Room, string? Comment);
}
