using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's school holidays (Ferien) and public holidays as
    /// Holiday agenda entries. Schulnetz's Mobile <c>/me/vacations</c> endpoint is empty;
    /// the holidays arrive in the Mobile agenda as all-day, course-less entries with
    /// <c>eventType == "Event"</c> (e.g. "Sommerferien 2026", "Karfreitag 2026").
    /// </summary>
    public class VacationsSyncService(ISchoolData schoolData, ILogger<VacationsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var events = await client.Api.Mobile.Agenda.GetAsync(cancellationToken: ct);
            if (events is null || events.Count == 0) return;

            var records = new List<AgendaRecord>();
            foreach (var e in events)
            {
                if (!string.Equals(e.EventType, "Event", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(e.CourseName) || !string.IsNullOrWhiteSpace(e.CourseToken)) continue;
                if (!TryParseDay(e.StartDate, out var from, out var allDay) || !allDay) continue;

                var title = string.IsNullOrWhiteSpace(e.Text) ? "Ferien" : e.Text!;
                DateOnly? to = TryParseDay(e.EndDate, out var end, out _) && end > from ? end : null;

                records.Add(new AgendaRecord(ExternalIds.Of("holiday", from.ToString("yyyy-MM-dd"), title), AgendaKind.Holiday, title, SwissTime.UtcDate(from), to is { } until ? SwissTime.UtcDate(until) : null));
            }

            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced holidays for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }

        private static bool TryParseDay(string? raw, out DateOnly day, out bool allDay)
        {
            day = default;
            allDay = false;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
            day = DateOnly.FromDateTime(parsed);
            allDay = parsed.TimeOfDay == TimeSpan.Zero;
            return true;
        }
    }
}
