using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>The current semester's grades from the mobile API, with the class average Schulnetz reports.</summary>
    public class MobileGradesSyncService(ISchoolData schoolData, ILogger<MobileGradesSyncService> logger)
    {
        public sealed record Result(IReadOnlyDictionary<string, string> SubjectsByCourse, IReadOnlySet<string> GradedExamIds, int Count);

        public async Task<Result> SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var grades = await client.Api.Mobile.Grades.GetAsync(cancellationToken: ct) ?? [];
            var subjects = new Dictionary<string, string>();
            var graded = new HashSet<string>();
            var records = new List<GradeRecord>();

            foreach (var grade in grades)
            {
                var course = Blank(grade.Course);
                var subject = Blank(grade.Subject) ?? "Default";
                if (course is not null)
                    subjects[course] = subject;
                if (Blank(grade.ExamId) is { } examId)
                    graded.Add(examId);
                if (grade.Mark is not { } mark || !DateOnly.TryParseExact(grade.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    continue;

                var examName = GradeNames.ExamName(grade.Title, date);
                records.Add(new GradeRecord(ExternalIds.Of(course ?? subject, examName), subject, examName, (decimal)mark, (decimal)(grade.Weight ?? 1), date)
                {
                    ClassAverage = grade.ClassGrade is { } average ? Math.Round((decimal)average, 3) : null,
                });
            }

            var synced = await schoolData.SyncGradesAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced mobile grades for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
            return new Result(subjects, graded, records.Count);
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>Exam names shared by the mobile and web grade sources, so both produce the same exam and external id.</summary>
    public static class GradeNames
    {
        public static string ExamName(string? title, DateOnly? date)
        {
            var topic = string.IsNullOrWhiteSpace(title) ? "Prüfung" : title.Trim();
            return date is { } d ? $"{topic} ({d.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)})" : topic;
        }
    }
}
