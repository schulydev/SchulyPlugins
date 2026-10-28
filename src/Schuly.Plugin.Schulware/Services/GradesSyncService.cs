using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Grades scraped from the Schulnetz web page: the whole history for schools without the mobile API, otherwise only past semesters.</summary>
    public class GradesSyncService(ISchoolData schoolData, ILogger<GradesSyncService> logger)
    {
        public sealed record Result(IReadOnlyDictionary<string, string> SubjectsByCourse, int Count);

        public async Task<Result> SyncAsync(SchulwareApiClient client, SchulwareAccount account, IReadOnlyCollection<string>? semesters, string? skipSemester, CancellationToken ct)
        {
            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "grades",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
                Semesters = semesters?.ToList(),
            }, cancellationToken: ct);

            if (result is null || result.Success != true)
            {
                account.WebSessionId = null;
                throw new WebSessionExpiredException(result?.Message);
            }

            var subjects = new Dictionary<string, string>();
            var records = new List<GradeRecord>();
            foreach (var course in result.Grades?.Courses ?? [])
            {
                var subject = string.IsNullOrWhiteSpace(course.Course) ? "Default" : course.Course!;
                if (!string.IsNullOrWhiteSpace(course.CourseToken))
                    subjects[course.CourseToken!] = subject;
                if (skipSemester is not null && string.Equals(course.Semester?.Trim(), skipSemester, StringComparison.Ordinal))
                    continue;

                foreach (var entry in course.Exams ?? [])
                {
                    if (entry.Mark is not { } mark) continue;
                    DateOnly? date = DateOnly.TryParseExact(entry.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                    var examName = GradeNames.ExamName(entry.Topic, date);
                    records.Add(new GradeRecord(ExternalIds.Of(course.CourseToken ?? subject, examName), subject, examName, (decimal)mark, (decimal)(entry.Weight ?? 1), date)
                    {
                        ClassAverage = entry.ClassAverage is { } average ? Math.Round((decimal)average, 3) : null,
                    });
                }
            }

            var synced = await schoolData.SyncGradesAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced web grades for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
            return new Result(subjects, records.Count);
        }
    }

    public class WebSessionExpiredException(string? message) : Exception(message ?? "The Schulnetz web session expired.");
}
