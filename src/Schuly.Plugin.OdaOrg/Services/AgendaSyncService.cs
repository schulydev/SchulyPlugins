using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.OdaOrg.Services
{
    public class AgendaSyncService(ISchoolData schoolData, ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(OdaOrgAccount account, IReadOnlyList<CourseDay> days, CancellationToken ct)
        {
            if (account.SchoolUserId is null || days.Count == 0) return;

            var records = days.Select(d => new AgendaRecord(
                ExternalIds.Of("course-day", d.Date.ToString("O"), d.Course),
                AgendaKind.Event,
                string.IsNullOrWhiteSpace(d.Topic) ? d.Course : d.Topic!,
                d.Date,
                d.EndDate,
                d.Room,
                d.Instructor is not null ? $"{d.Course} — {d.Instructor}" : d.Course)).ToList();

            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced OdaOrg course days for account {Account}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }
    }
}
