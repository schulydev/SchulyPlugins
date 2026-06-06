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
    /// Syncs a Schulware account's absences into the main Schuly DB via the
    /// Mobile absences endpoint. Deduplicates on SchoolUserId + From + Until.
    /// (Absences stay on the Mobile API even when the account has a web session —
    /// that session is reserved for the scraper-only document pages.)
    /// </summary>
    public class AbsencesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AbsencesSyncService> logger)
    {
        private record AbsenceRow(string? From, string? To, string? Reason);

        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var rows = await FromMobileAsync(client, ct);

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var row in rows)
            {
                if (!TryParseDate(row.From, out var from) || !TryParseDate(row.To, out var to)) continue;

                var exists = await mainDb.Absences
                    .AnyAsync(a => a.SchoolUserId == schoolUserId && a.From == from && a.Until == to, ct);
                if (exists) continue;

                mainDb.Absences.Add(new Absence
                {
                    SchoolUserId = schoolUserId,
                    From = from,
                    Until = to,
                    Reason = row.Reason ?? "Imported from Schulnetz",
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

        private async Task<List<AbsenceRow>> FromMobileAsync(SchulwareApiClient client, CancellationToken ct)
        {
            var absences = await client.Api.Mobile.Absences.GetAsync(cancellationToken: ct);
            return absences?.Select(a => new AbsenceRow(a.DateFrom, a.DateTo, a.Reason)).ToList() ?? [];
        }

        private static bool TryParseDate(string? raw, out DateTime date)
        {
            // Scraped dates look like "Do, 05.03.2026"; Mobile uses ISO "2026-03-05".
            date = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var cleaned = raw.Contains(',') ? raw[(raw.IndexOf(',') + 1)..].Trim() : raw.Trim();
            if (DateTime.TryParse(cleaned, System.Globalization.CultureInfo.GetCultureInfo("de-CH"),
                    System.Globalization.DateTimeStyles.None, out date)
                || DateTime.TryParse(cleaned, out date))
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                return true;
            }
            return false;
        }
    }
}
