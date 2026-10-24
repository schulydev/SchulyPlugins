using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    public class GradesSyncService(ISchoolData schoolData, ILogger<GradesSyncService> logger)
    {
        public async Task<Dictionary<string, string>> SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId))
            {
                logger.LogWarning("Account {AccountId} has no web session; skipping grade scrape", account.Id);
                return new();
            }

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "grades",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            // On the grades page Success=false only means the web session is dead.
            if (result is null || result.Success != true)
            {
                account.WebSessionId = null;
                throw new InvalidOperationException(
                    result?.Message ?? "Schulnetz web session expired; re-authenticate to resume grade sync.");
            }

            var courses = result.Grades?.Courses;
            if (courses is null || courses.Count == 0) return new();

            var subjectByToken = new Dictionary<string, string>();
            foreach (var c in courses)
                if (!string.IsNullOrWhiteSpace(c.CourseToken) && !string.IsNullOrWhiteSpace(c.Course))
                    subjectByToken[c.CourseToken!] = c.Course!;

            var records = new List<GradeRecord>();
            foreach (var course in courses)
            {
                if (course.Exams is null) continue;
                var subject = string.IsNullOrWhiteSpace(course.Course) ? "Default" : course.Course!;

                foreach (var entry in course.Exams)
                {
                    if (entry.Mark is null) continue;

                    var examName = string.IsNullOrWhiteSpace(entry.Date)
                        ? (entry.Topic ?? "Prüfung")
                        : $"{entry.Topic ?? "Prüfung"} ({entry.Date})";

                    DateOnly? examDate = DateOnly.TryParseExact(
                        entry.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d : null;

                    records.Add(new GradeRecord(ExternalIds.Of(course.CourseToken ?? subject, examName), subject, examName, (decimal)entry.Mark.Value, (decimal)(entry.Weight ?? 1), examDate));
                }
            }

            var synced = await schoolData.SyncGradesAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced grades for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);

            return subjectByToken;
        }
    }
}
