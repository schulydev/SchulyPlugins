using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>One-time fix for lessons synced before Schulnetz times were converted to UTC.</summary>
    public static class LessonTimeCorrection
    {
        public const string MigrationName = "ConvertLessonTimesToUtc";

        public static async Task ApplyAsync(SchulwareDbContext db, Schuly.Infrastructure.SchulyDbContext mainDb, ILogger logger, CancellationToken ct)
        {
            var schoolUserIds = await db.Accounts
                .Where(a => a.SchoolUserId != null)
                .Select(a => a.SchoolUserId!.Value)
                .ToListAsync(ct);
            if (schoolUserIds.Count == 0) return;

            var lessons = await mainDb.AgendaEntries
                .Where(a => a.EntryType == AgendaEntryType.Lesson && a.SchoolUserId != null && schoolUserIds.Contains(a.SchoolUserId.Value))
                .ToListAsync(ct);

            foreach (var lesson in lessons)
            {
                if (SchulnetzTime.TryToUtc(lesson.Date, out var date))
                    lesson.Date = date;
                if (lesson.EndDate is { } endDate && SchulnetzTime.TryToUtc(endDate, out var end))
                    lesson.EndDate = end;
            }

            await mainDb.SaveChangesAsync(ct);
            logger.LogInformation("Converted {Count} Schulware lesson times to UTC", lessons.Count);
        }
    }
}
