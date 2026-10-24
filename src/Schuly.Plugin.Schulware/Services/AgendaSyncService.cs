using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    public class AgendaSyncService(ISchoolData schoolData, ILogger<AgendaSyncService> logger)
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

            var records = new List<AgendaRecord>();
            foreach (var ev in events)
            {
                if (!SwissTime.TryParseToUtc(ev.StartDate, out var start)) continue;

                static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
                string? mapped = null;
                if (Blank(ev.Kurskuerzel) is { } token)
                    subjectNames.TryGetValue(token, out mapped);
                var title = Blank(mapped) ?? Blank(ev.Fachkuerzel) ?? Blank(ev.Text)
                    ?? Blank(ev.Kurskuerzel) ?? Blank(ev.Klasse) ?? "Lektion";
                DateTimeOffset? end = SwissTime.TryParseToUtc(ev.EndDate, out var e) && e >= start ? e : null;
                var course = Blank(ev.Kurskuerzel) ?? Blank(ev.Fachkuerzel) ?? title;

                records.Add(new AgendaRecord(ExternalIds.Of("lesson", start.ToString("O"), course), AgendaKind.Lesson, title, start, end, ev.Zimmerkuerzel ?? ev.Zimmer, string.IsNullOrWhiteSpace(ev.Kommentar) ? ev.Lehrerkuerzelname : ev.Kommentar));
            }

            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced agenda for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }
    }
}
