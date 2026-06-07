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
    /// Pulls a Schulware account's grades by scraping the Schulnetz "Noten" page
    /// (typed <see cref="GradesPageDto"/>) and merges them into the main Schuly DB.
    /// Creates the backing <c>Exam</c> and <c>Class</c> rows on demand. Requires a
    /// captured web session on the account.
    /// </summary>
    public class GradesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<GradesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId))
            {
                logger.LogWarning("Account {AccountId} has no web session; skipping grade scrape", account.Id);
                return;
            }

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "grades",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var courses = result?.Grades?.Courses;
            if (courses is null || courses.Count == 0) return;

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

                    var exam = await FindOrCreateExamAsync(course.Course, examName, schoolUserId, ct);
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
