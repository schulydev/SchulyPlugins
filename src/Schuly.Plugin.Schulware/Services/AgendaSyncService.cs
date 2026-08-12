using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    public class AgendaSyncService(Schuly.Infrastructure.SchulyDbContext mainDb, ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, IReadOnlyDictionary<string, string> subjectNames, CancellationToken ct)
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
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var events = result?.Schedule;
            if (events is null || events.Count == 0) return;

            var schoolUserId = account.SchoolUserId!.Value;
            var synced = 0;

            foreach (var ev in events)
            {
                if (!TryParse(ev.StartDate, out var date)) continue;

                static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
                string? mapped = null;
                if (Blank(ev.Kurskuerzel) is { } token)
                    subjectNames.TryGetValue(token, out mapped);
                var title = Blank(mapped) ?? Blank(ev.Fachkuerzel) ?? Blank(ev.Text)
                    ?? Blank(ev.Kurskuerzel) ?? Blank(ev.Klasse) ?? "Lektion";

                var exists = await mainDb.AgendaEntries.AnyAsync(
                    a => a.SchoolUserId == schoolUserId && a.Date == date && a.Title == title, ct);
                if (exists) continue;

                mainDb.AgendaEntries.Add(new AgendaEntry
                {
                    EntryType = AgendaEntryType.Lesson,
                    Title = title,
                    Description = string.IsNullOrWhiteSpace(ev.Kommentar) ? ev.Lehrerkuerzelname : ev.Kommentar,
                    Place = ev.Zimmerkuerzel ?? ev.Zimmer,
                    Date = date,
                    EndDate = TryParse(ev.EndDate, out var end) && end >= date ? end : null,
                    SchoolUserId = schoolUserId,
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} agenda entries for account {AccountId}", synced, account.Id);
            }
        }

        private static bool TryParse(string? raw, out DateTime date)
        {
            if (DateTime.TryParse(raw, out date))
            {
                date = DateTime.SpecifyKind(date, DateTimeKind.Utc);
                return true;
            }
            date = default;
            return false;
        }
    }
}
