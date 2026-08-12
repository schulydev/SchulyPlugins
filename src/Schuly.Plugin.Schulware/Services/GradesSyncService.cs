using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    public class GradesSyncService(Schuly.Infrastructure.SchulyDbContext mainDb, ILogger<GradesSyncService> logger)
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

            // For the grades page, Success=false only ever means the web session
            // (PHPSESSID) is dead — it can't be refreshed like the mobile token.
            // Drop it to force a re-capture and surface the failure, rather than
            // silently reporting a successful, empty sync.
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

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var course in courses)
            {
                if (course.Exams is null) continue;

                foreach (var entry in course.Exams)
                {
                    if (entry.Mark is null) continue;

                    var examName = string.IsNullOrWhiteSpace(entry.Date)
                        ? (entry.Topic ?? "Prüfung")
                        : $"{entry.Topic ?? "Prüfung"} ({entry.Date})";

                    DateOnly? examDate = DateOnly.TryParseExact(
                        entry.Date, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                        ? d : null;

                    var exam = await FindOrCreateExamAsync(course.Course, examName, examDate, schoolUserId, ct);
                    var score = (decimal)entry.Mark.Value;
                    var weighting = (decimal)(entry.Weight ?? 1);

                    var existing = await mainDb.Grades
                        .FirstOrDefaultAsync(g => g.SchoolUserId == schoolUserId && g.ExamId == exam.Id, ct);

                    if (existing is null)
                    {
                        mainDb.Grades.Add(new Grade
                        {
                            SchoolUserId = schoolUserId,
                            ExamId = exam.Id,
                            Score = score,
                            Weighting = weighting,
                        });
                        synced++;
                    }
                    else if (existing.Score != score)
                    {
                        existing.Score = score;
                        existing.Weighting = weighting;
                        synced++;
                    }
                }
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} grades for account {AccountId}", synced, account.Id);
            }

            return subjectByToken;
        }

        private async Task<Exam> FindOrCreateExamAsync(string? courseName, string examName, DateOnly? examDate, Guid schoolUserId, CancellationToken ct)
        {
            var schoolUser = await mainDb.SchoolUsers
                .Include(su => su.Classes)
                .FirstOrDefaultAsync(su => su.Id == schoolUserId, ct);

            Class? cls = null;
            if (schoolUser is not null)
            {
                var className = string.IsNullOrWhiteSpace(courseName) ? "Default" : courseName;

                cls = await mainDb.Classes
                    .FirstOrDefaultAsync(c => c.Name == className && c.SchoolId == schoolUser.SchoolId, ct);
                if (cls is null)
                {
                    cls = new Class { Name = className, SchoolId = schoolUser.SchoolId };
                    mainDb.Classes.Add(cls);
                    await mainDb.SaveChangesAsync(ct);
                }

                if (!schoolUser.Classes.Any(c => c.Id == cls.Id))
                {
                    schoolUser.Classes.Add(cls);
                    await mainDb.SaveChangesAsync(ct);
                }
            }

            var classId = cls?.Id ?? Guid.Empty;
            if (classId == Guid.Empty)
                return new Exam { Id = Guid.NewGuid(), Name = examName, Type = ExamType.Classic, Date = examDate, ClassId = classId };

            var existing = await mainDb.Exams
                .FirstOrDefaultAsync(e => e.Name == examName && e.ClassId == classId, ct);
            if (existing is not null)
            {
                if (existing.Date is null && examDate is not null)
                    existing.Date = examDate;
                return existing;
            }

            var exam = new Exam { Name = examName, Type = ExamType.Classic, Date = examDate, ClassId = classId };
            mainDb.Exams.Add(exam);
            await mainDb.SaveChangesAsync(ct);
            return exam;
        }
    }
}
