using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Kiota.Abstractions.Serialization;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs the school's holidays ("Ferienübersicht") and events ("Termine") from the Schulnetz home page. The mobile
    /// agenda only reaches a few weeks ahead; the home page lists the whole school year. Tables and columns are found by
    /// their headings, and the ids match <see cref="VacationsSyncService"/> so both sources update the same entries.
    /// </summary>
    public class HomeEventsSyncService(ISchoolData schoolData, ILogger<HomeEventsSyncService> logger)
    {
        public async Task<int> SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "home",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);
            if (result?.Success != true)
                throw new WebSessionExpiredException(result?.Message);

            var tables = (result.Home?.Tables ?? []).Select(t => (t.Name ?? "", (t.Rows ?? []).Select(Cells).ToList())).ToList();
            var records = FromHome(tables);
            if (records.Count == 0) return 0;
            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced home page holidays and events for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
            return records.Count;
        }

        private static Dictionary<string, string> Cells(ScrapedTableDto_rows row) =>
            row.AdditionalData.ToDictionary(kv => kv.Key.Trim(), kv => (kv.Value is UntypedString s ? s.GetValue() : kv.Value?.ToString())?.Trim() ?? "", StringComparer.OrdinalIgnoreCase);

        /// <summary>Holidays from a table named "Ferien…" (Bezeichnung/Von/Bis), events from one named "Termin…" (Von/Bis/Ort/Beschreibung).</summary>
        public static List<AgendaRecord> FromHome(IEnumerable<(string Name, List<Dictionary<string, string>> Rows)> tables)
        {
            var records = new List<AgendaRecord>();
            foreach (var (name, rows) in tables)
            {
                var holidays = name.Contains("Ferien", StringComparison.OrdinalIgnoreCase);
                if (!holidays && !name.Contains("Termin", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var row in rows)
                {
                    var title = Cell(row, holidays ? "Bezeichnung" : "Beschreibung") ?? Cell(row, "Bezeichnung") ?? Cell(row, "Beschreibung");
                    if (title is null || !TryParse(Cell(row, "Von"), out var from, out var fromTime)) continue;
                    var hasTo = TryParse(Cell(row, "Bis"), out var to, out var toTime);
                    if (fromTime is null)
                    {
                        DateOnly? until = hasTo && to > from ? to : null;
                        var kind = holidays || VacationsSyncService.IsDayOff(title) ? AgendaKind.Holiday : AgendaKind.Event;
                        records.Add(new AgendaRecord(ExternalIds.Of("holiday", from.ToString("yyyy-MM-dd"), title), kind, title, SwissTime.UtcDate(from), until is { } u ? SwissTime.UtcDate(u) : null, Cell(row, "Ort")));
                        continue;
                    }
                    if (!SwissTime.TryToUtc(from.ToDateTime(fromTime.Value), out var start)) continue;
                    DateTimeOffset? end = hasTo && SwissTime.TryToUtc(to.ToDateTime(toTime ?? TimeOnly.MinValue), out var e) && (toTime is not null || to > from) && e >= start ? e : null;
                    records.Add(new AgendaRecord(ExternalIds.Of("event", start.ToString("O"), title), AgendaKind.Event, title, start, end, Cell(row, "Ort")));
                }
            }
            return records;
        }

        private static string? Cell(Dictionary<string, string> row, string column) =>
            row.TryGetValue(column, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

        private static bool TryParse(string? raw, out DateOnly day, out TimeOnly? time)
        {
            day = default;
            time = null;
            if (raw is null) return false;
            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!DateOnly.TryParseExact(parts[0], "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out day)) return false;
            if (parts.Length > 1 && TimeOnly.TryParseExact(parts[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) time = t;
            return true;
        }
    }
}
