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
    /// Pulls a Schulware account's agenda by scraping the Schulnetz scheduler
    /// (typed <see cref="ScheduleEventDto"/> list via the "schedule" page) and
    /// inserts new <see cref="AgendaEntry"/> rows in the main DB. Dedup is on
    /// (ClassId, Date, Title). Events without a resolvable class are skipped —
    /// <c>AgendaEntry.ClassId</c> is required. Requires a captured web session.
    /// </summary>
    public class AgendaSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId))
            {
                logger.LogWarning("Account {AccountId} has no web session; skipping agenda scrape", account.Id);
                return;
            }

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "schedule",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
            }, cancellationToken: ct);

            var events = result?.Schedule;
            if (events is null || events.Count == 0) return;

            var schoolUser = await mainDb.SchoolUsers
                .Include(su => su.Classes)
                .FirstOrDefaultAsync(su => su.Id == account.SchoolUserId!.Value, ct);
            if (schoolUser is null) return;

            var classesByName = schoolUser.Classes.ToDictionary(c => c.Name, c => c);
            var synced = 0;

            foreach (var ev in events)
            {
                if (!TryParseStart(ev.StartDate, out var date)) continue;

                var title = ev.Text ?? ev.Kurskuerzel ?? "Untitled";
                var cls = ResolveClass(ev, classesByName);
                if (cls is null) continue; // unknown course → skip rather than orphan

                var exists = await mainDb.AgendaEntries.AnyAsync(
                    a => a.ClassId == cls.Id && a.Date == date && a.Title == title, ct);
                if (exists) continue;

                mainDb.AgendaEntries.Add(new AgendaEntry
                {
                    ClassId = cls.Id,
                    Date = date,
                    Title = title,
                    Description = ev.Kommentar,
                    Place = ev.Zimmerkuerzel ?? ev.Zimmer,
                    EntryType = MapType(ev.EventType),
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} agenda entries for account {AccountId}", synced, account.Id);
            }
        }

        private static Class? ResolveClass(ScheduleEventDto ev, IReadOnlyDictionary<string, Class> classesByName)
        {
            if (!string.IsNullOrWhiteSpace(ev.Kurskuerzel) && classesByName.TryGetValue(ev.Kurskuerzel, out var byToken))
                return byToken;
            if (!string.IsNullOrWhiteSpace(ev.Klasse) && classesByName.TryGetValue(ev.Klasse, out var byClass))
                return byClass;
            return null;
        }

        private static bool TryParseStart(string? raw, out DateTime date)
        {
            if (DateTime.TryParse(raw, out date))
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                return true;
            }
            date = default;
            return false;
        }

        private static AgendaEntryType MapType(string? schulnetzType) => schulnetzType?.ToLowerInvariant() switch
        {
            "test" or "exam" or "prüfung" => AgendaEntryType.Test,
            "lesson" or "unterricht" => AgendaEntryType.Lesson,
            _ => AgendaEntryType.Event,
        };
    }
}
