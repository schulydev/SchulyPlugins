using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Merges scraped module grades into the main DB. Each OdaOrg module maps to
    /// a <see cref="Class"/> + <see cref="Exam"/>; the final mark becomes a
    /// <see cref="Grade"/>. Mirrors the Schulware plugin's grade-sync shape.
    /// </summary>
    public class GradesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<GradesSyncService> logger)
    {
        public async Task SyncAsync(OdaOrgAccount account, IReadOnlyList<ModuleGrade> grades, CancellationToken ct)
        {
            if (account.SchoolUserId is null || grades.Count == 0) return;
            var schoolUserId = account.SchoolUserId.Value;

            var schoolUser = await mainDb.SchoolUsers
                .Include(su => su.Classes)
                .FirstOrDefaultAsync(su => su.Id == schoolUserId, ct);
            if (schoolUser is null) return;

            var synced = 0;
            foreach (var g in grades)
            {
                var cls = await GetOrCreateClassAsync(schoolUser, g.ModuleName, ct);
                var exam = await GetOrCreateExamAsync(g.ModuleName, cls.Id, ct);

                var existing = await mainDb.Grades
                    .FirstOrDefaultAsync(x => x.SchoolUserId == schoolUserId && x.ExamId == exam.Id, ct);
                if (existing is null)
                {
                    mainDb.Grades.Add(new Grade
                    {
                        SchoolUserId = schoolUserId,
                        ExamId = exam.Id,
                        Score = g.FinalGrade,
                        Weighting = 1,
                    });
                    synced++;
                }
                else if (existing.Score != g.FinalGrade)
                {
                    existing.Score = g.FinalGrade;
                    synced++;
                }
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} OdaOrg grades for account {Account}", synced, account.Id);
            }
        }

        private async Task<Class> GetOrCreateClassAsync(SchoolUser schoolUser, string name, CancellationToken ct)
        {
            var cls = await mainDb.Classes.FirstOrDefaultAsync(c => c.Name == name && c.SchoolId == schoolUser.SchoolId, ct);
            if (cls is null)
            {
                cls = new Class { Name = name, SchoolId = schoolUser.SchoolId };
                mainDb.Classes.Add(cls);
                await mainDb.SaveChangesAsync(ct);
            }
            if (!schoolUser.Classes.Any(c => c.Id == cls.Id))
            {
                schoolUser.Classes.Add(cls);
                await mainDb.SaveChangesAsync(ct);
            }
            return cls;
        }

        private async Task<Exam> GetOrCreateExamAsync(string name, Guid classId, CancellationToken ct)
        {
            var exam = await mainDb.Exams.FirstOrDefaultAsync(e => e.Name == name && e.ClassId == classId, ct);
            if (exam is null)
            {
                exam = new Exam { Name = name, Type = ExamType.Classic, ClassId = classId };
                mainDb.Exams.Add(exam);
                await mainDb.SaveChangesAsync(ct);
            }
            return exam;
        }
    }
}
