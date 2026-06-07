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
    /// Pulls a Schulware account's absences by scraping the Schulnetz "Absenzen"
    /// page (typed <see cref="AbsencesPageDto"/>) and inserts new rows into the
    /// main Schuly DB (deduplicates on SchoolUserId + From + Until). Requires a
    /// captured web session on the account.
    /// </summary>
    public class AbsencesSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AbsencesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId))
            {
                logger.LogWarning("Account {AccountId} has no web session; skipping absence scrape", account.Id);
                return;
            }

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "absences",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var absences = result?.Absences?.Absences;
            if (absences is null || absences.Count == 0) return;

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var absence in absences)
            {
                if (!TryParseDate(absence.DateFrom, out var from) || !TryParseDate(absence.DateTo, out var to))
                    continue;

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

        private static bool TryParseDate(string? raw, out DateTime date)
        {
            // Scraped dates look like "Do, 05.03.2026" or "05.03.2026".
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
