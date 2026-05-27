using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Pulls a Schulware account's grades from SchulwareAPI and merges them
    /// into the main Schuly DB. Creates the backing <c>Exam</c> and <c>Class</c>
    /// rows on demand.
    /// </summary>
    public class GradesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<GradesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var grades = await client.Api.Mobile.Grades.GetAsync(cancellationToken: ct);
            if (grades is null || grades.Count == 0) return;

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var grade in grades)
            {
                if (grade.Mark is null || grade.ExamId is null) continue;

                var exam = await FindOrCreateExamAsync(grade, schoolUserId, ct);
                var existing = await mainDb.Grades
                    .FirstOrDefaultAsync(g => g.SchoolUserId == schoolUserId && g.ExamId == exam.Id, ct);

                if (existing is null)
                {
                    mainDb.Grades.Add(new Grade
                    {
                        SchoolUserId = schoolUserId,
                        ExamId = exam.Id,
                        Score = (decimal)grade.Mark,
                        Weighting = (decimal)(grade.Weight ?? 1),
                    });
                    synced++;
                }
                else if (existing.Score != (decimal)grade.Mark)
                {
                    existing.Score = (decimal)grade.Mark;
                    existing.Weighting = (decimal)(grade.Weight ?? 1);
                    synced++;
                }
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} grades for account {AccountId}", synced, account.Id);
            }
        }

        private async Task<Exam> FindOrCreateExamAsync(Client.Models.GradeDto grade, Guid schoolUserId, CancellationToken ct)
        {
            var examName = grade.Title ?? grade.Subject ?? $"Exam {grade.ExamId}";

            var schoolUser = await mainDb.SchoolUsers
                .Include(su => su.Classes)
                .FirstOrDefaultAsync(su => su.Id == schoolUserId, ct);

            var cls = schoolUser?.Classes.FirstOrDefault();
            if (cls is null && schoolUser is not null)
            {
                var className = grade.Course ?? grade.Subject ?? "Default";
                cls = await mainDb.Classes
                    .FirstOrDefaultAsync(c => c.Name == className && c.SchoolId == schoolUser.SchoolId, ct);
                if (cls is null)
                {
                    cls = new Class { Name = className, SchoolId = schoolUser.SchoolId };
                    mainDb.Classes.Add(cls);
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
