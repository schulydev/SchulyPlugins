using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Controllers
{
    /// <summary>
    /// Stateless, account-free OdaOrg proxy for the app's private mode. One scrape
    /// pass returns the user's profile, grades and course days mapped to flat DTOs.
    /// Persists nothing: the caller passes credentials in per request.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/odaorg/stateless")]
    public class OdaorgStatelessController(OdaOrgScraper scraper) : ControllerBase
    {
        [HttpPost("data")]
        [ProducesResponseType(typeof(OdaorgStatelessData), StatusCodes.Status200OK)]
        public async Task<IActionResult> Data([FromBody] OdaorgStatelessRequest request, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(request.Username)
                || string.IsNullOrWhiteSpace(request.Password)
                || string.IsNullOrWhiteSpace(request.BaseUrl))
                return BadRequest("username, password and baseUrl are required");

            if (!BaseUrlGuard.IsAllowed(request.BaseUrl))
                return BadRequest("baseUrl is not an allowed target");

            OdaScrape? scrape;
            try
            {
                scrape = await scraper.ScrapeAsync(request.BaseUrl, request.Username, request.Password, ct);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }
            if (scrape is null)
                return Unauthorized("OdaOrg login or scrape failed");

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

    // Field names match the app's flat private DTOs (camelCase JSON).
    public record OdaorgStatelessGrade(string? Id, string? ExamId, string? Subject, double? Score);

    public record OdaorgStatelessExam(string? Id, string? Name, string? Subject);

    public record OdaorgStatelessAgendaEvent(string? Id, string? Title, string? StartDate, string? EndDate, string? Room, string? Comment);
}
