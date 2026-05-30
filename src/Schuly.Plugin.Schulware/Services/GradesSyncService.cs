using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's grades into the main Schuly DB. Prefers the
    /// typed web scraper (richer "Noten" page) when the account has a captured
    /// web session; otherwise falls back to the Mobile grades endpoint. Creates
    /// the backing <c>Exam</c> and <c>Class</c> rows on demand.
    /// </summary>
    public class GradesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<GradesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var schoolUserId = account.SchoolUserId!.Value;
            var synced = string.IsNullOrEmpty(account.WebSessionId)
                ? await SyncViaMobileAsync(client, schoolUserId, ct)
                : await SyncViaScrapeAsync(client, account, schoolUserId, ct);

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} grades for account {AccountId}", synced, account.Id);
            }
        }

        // --- Web scraper (typed Noten page) -----------------------------------

        private async Task<int> SyncViaScrapeAsync(SchulwareApiClient client, SchulwareAccount account, Guid schoolUserId, CancellationToken ct)
        {
            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "grades",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
            }, cancellationToken: ct);

            var courses = result?.Grades?.Courses;
            if (courses is null || courses.Count == 0) return 0;

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
                    var exam = await FindOrCreateExamAsync(course.Course, examName, schoolUserId, ct);
                    synced += await UpsertGradeAsync(schoolUserId, exam.Id, (decimal)entry.Mark.Value, (decimal)(entry.Weight ?? 1), ct);
                }
            }
            return synced;
        }

        // --- Mobile grades endpoint (fallback) --------------------------------

        private async Task<int> SyncViaMobileAsync(SchulwareApiClient client, Guid schoolUserId, CancellationToken ct)
        {
            var grades = await client.Api.Mobile.Grades.GetAsync(cancellationToken: ct);
            if (grades is null || grades.Count == 0) return 0;

            var synced = 0;
            foreach (var grade in grades)
            {
                if (grade.Mark is null || grade.ExamId is null) continue;
                var examName = grade.Title ?? grade.Subject ?? $"Exam {grade.ExamId}";
                var exam = await FindOrCreateExamAsync(grade.Course ?? grade.Subject, examName, schoolUserId, ct);
                synced += await UpsertGradeAsync(schoolUserId, exam.Id, (decimal)grade.Mark.Value, (decimal)(grade.Weight ?? 1), ct);
            }
            return synced;
        }

        private async Task<int> UpsertGradeAsync(Guid schoolUserId, Guid examId, decimal score, decimal weighting, CancellationToken ct)
        {
            var existing = await mainDb.Grades
                .FirstOrDefaultAsync(g => g.SchoolUserId == schoolUserId && g.ExamId == examId, ct);
            if (existing is null)
            {
                mainDb.Grades.Add(new Grade { SchoolUserId = schoolUserId, ExamId = examId, Score = score, Weighting = weighting });
                return 1;
            }
            if (existing.Score != score)
            {
                existing.Score = score;
                existing.Weighting = weighting;
                return 1;
            }
            return 0;
        }

        private async Task<Exam> FindOrCreateExamAsync(string? courseName, string examName, Guid schoolUserId, CancellationToken ct)
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
                return new Exam { Id = Guid.NewGuid(), Name = examName, Type = ExamType.Classic, ClassId = classId };

            var existing = await mainDb.Exams
                .FirstOrDefaultAsync(e => e.Name == examName && e.ClassId == classId, ct);
            if (existing is not null) return existing;

            var exam = new Exam { Name = examName, Type = ExamType.Classic, ClassId = classId };
            mainDb.Exams.Add(exam);
            await mainDb.SaveChangesAsync(ct);
            return exam;
        }
    }
}
