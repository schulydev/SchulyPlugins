using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Pulls a Schulware account's absences from SchulwareAPI and inserts new
    /// rows into the main Schuly DB (deduplicates on SchoolUserId + From + Until).
    /// </summary>
    public class AbsencesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AbsencesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var absences = await client.Api.Mobile.Absences.GetAsync(cancellationToken: ct);
            if (absences is null || absences.Count == 0) return;

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var absence in absences)
            {
                if (absence.DateFrom is null || absence.DateTo is null) continue;
                if (!DateTime.TryParse(absence.DateFrom, out var from) ||
                    !DateTime.TryParse(absence.DateTo, out var to)) continue;

                from = DateTime.SpecifyKind(from, DateTimeKind.Utc);
                to = DateTime.SpecifyKind(to, DateTimeKind.Utc);

                var existing = await mainDb.Absences
                    .FirstOrDefaultAsync(a => a.SchoolUserId == schoolUserId
                        && a.From == from && a.Until == to, ct);
                if (existing is not null) continue;

                mainDb.Absences.Add(new Absence
                {
                    SchoolUserId = schoolUserId,
                    From = from,
                    Until = to,
                    Reason = absence.Reason ?? "Imported from Schulnetz",
                    Type = AbsenceType.Absence,
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} absences for account {AccountId}", synced, account.Id);
            }
        }
    }
}
