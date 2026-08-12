using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Controllers
{
    [ApiController]
    [AllowAnonymous]
    [Route("api/plugins/schulware/stateless")]
    public class StatelessDataController(IHttpClientFactory httpClientFactory, IConfiguration configuration) : ControllerBase
    {
        private string ProxyBaseUrl =>
            configuration["Schulware:DefaultApiBaseUrl"] ?? "https://schlwr.pianonic.ch";

        [HttpGet("grades")]
        public Task<IActionResult> Grades() => ProxyAsync(async c =>
            (await c.Api.Mobile.Grades.GetAsync() ?? []).Select(MapGrade).ToList());

        [HttpGet("exams")]
        public Task<IActionResult> Exams() => ProxyAsync(async c =>
            (await c.Api.Mobile.Exams.GetAsync() ?? []).Select(MapExam).ToList());

        [HttpGet("absences")]
        public Task<IActionResult> Absences() => ProxyAsync(async c =>
            (await c.Api.Mobile.Absences.GetAsync() ?? []).Select(MapAbsence).ToList());

        [HttpGet("agenda")]
        public Task<IActionResult> Agenda() => ProxyAsync(async c =>
            (await c.Api.Mobile.Agenda.GetAsync() ?? []).Select(MapEvent).ToList());

        [HttpGet("userinfo")]
        public Task<IActionResult> UserInfo() => ProxyAsync(async c =>
            MapUserInfo(await c.Api.Mobile.UserInfo.GetAsync()));

        private async Task<IActionResult> ProxyAsync<T>(Func<SchulwareApiClient, Task<T>> fetch)
        {
            var token = Request.Headers["X-Plugin-Token"].ToString();
            var schulnetzBaseUrl = Request.Headers["X-Provider-Base-Url"].ToString();
            if (string.IsNullOrWhiteSpace(token))
                return BadRequest("Missing X-Plugin-Token header");
            if (string.IsNullOrWhiteSpace(schulnetzBaseUrl))
                return BadRequest("Missing X-Provider-Base-Url header");

            try
            {
                var client = SchulwareApiClientFactory.Create(
                    httpClientFactory, ProxyBaseUrl, schulnetzBaseUrl, token);
                return Ok(await fetch(client));
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { error = ex.Message });
            }
        }

        private static StatelessGradeDto MapGrade(GradeDto g) => new(
            g.Id, g.ExamId, g.Subject ?? g.Course, g.Mark, g.Date, g.Comment, g.Points);

        private static StatelessExamDto MapExam(ExamDto e) => new(
            e.Id,
            string.IsNullOrWhiteSpace(e.Text) ? e.CourseName : e.Text,
            e.CourseName, e.StartDate, e.EndDate, e.RoomToken, e.Comment, e.EventType);

        private static StatelessAbsenceDto MapAbsence(AbsenceDto a) => new(
            a.Id, a.DateFrom, a.DateTo, a.Reason ?? a.Category ?? a.Comment, a.Subject, a.IsExcused);

        private static StatelessAgendaEventDto MapEvent(EventDto e) => new(
            e.Id, e.CourseName ?? e.Text ?? e.TimetableText, e.StartDate, e.EndDate, e.RoomToken, e.EventType, e.Comment);

        private static StatelessUserInfoDto MapUserInfo(UserInfoDto? u) => new(
            u?.FirstName, u?.LastName, u?.Email, u?.Birthday, u?.EntryDate);
    }
}
