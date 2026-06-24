using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Inserts scraped ÜK course days (past + upcoming) as <see cref="AgendaEntry"/>
    /// rows attached to the student. Dedup on (SchoolUserId, Date, Title).
    /// </summary>
    public class AgendaSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(OdaOrgAccount account, IReadOnlyList<CourseDay> days, CancellationToken ct)
        {
            if (account.SchoolUserId is null || days.Count == 0) return;
            var schoolUserId = account.SchoolUserId.Value;

            var schoolUser = await mainDb.SchoolUsers.FirstOrDefaultAsync(su => su.Id == schoolUserId, ct);
            if (schoolUser is null) return;

            var synced = 0;
            foreach (var d in days)
            {
                var title = string.IsNullOrWhiteSpace(d.Topic) ? d.Course : d.Topic!;

                var exists = await mainDb.AgendaEntries.AnyAsync(
                    a => a.SchoolUserId == schoolUserId && a.Date == d.Date && a.Title == title, ct);
                if (exists) continue;

                mainDb.AgendaEntries.Add(new AgendaEntry
                {
                    // Exactly one scope must be set (CK_AgendaEntry_ExactlyOneScope):
                    // a personal agenda entry is scoped to the school user only.
                    SchoolUserId = schoolUserId,
                    Title = title,
                    Description = d.Instructor is not null ? $"{d.Course} — {d.Instructor}" : d.Course,
                    Place = d.Room,
                    Date = d.Date,
                    EndDate = d.EndDate,
                    EntryType = AgendaEntryType.Event,
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} OdaOrg course days for account {Account}", synced, account.Id);
            }
        }
    }
}
