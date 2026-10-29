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
    /// <c>eventType == "Event"</c> (e.g. "Sommerferien 2026", "Karfreitag 2026"). Other all-day school events arrive the
    /// same way (e.g. "Kulturwoche (Theater)"), so only titles that name a day off become Holiday; the rest become Event.
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

                // The id keeps its "holiday" prefix so entries synced before this distinction are updated, not duplicated.
                records.Add(new AgendaRecord(ExternalIds.Of("holiday", from.ToString("yyyy-MM-dd"), title), IsDayOff(title) ? AgendaKind.Holiday : AgendaKind.Event, title, SwissTime.UtcDate(from), to is { } until ? SwissTime.UtcDate(until) : null));
            }

            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced holidays for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }

        private static readonly string[] DayOffWords =
        [
            "ferien", "feiertag", "schulfrei", "unterrichtsfrei", "frei", "brückentag", "brueckentag", "vacances", "vacanze", "congé", "conge",
            "neujahr", "berchtold", "karfreitag", "ostern", "ostermontag", "tag der arbeit", "auffahrt", "pfingst", "fronleichnam",
            "bundesfeier", "nationalfeiertag", "maria himmelfahrt", "allerheiligen", "maria empfängnis", "weihnacht", "stephan",
        ];

        /// <summary>Whether an all-day, course-less entry is a day off (school holidays or a public holiday) rather than a school event.</summary>
        internal static bool IsDayOff(string title)
        {
            var text = title.ToLowerInvariant();
            return DayOffWords.Any(word => word == "frei" ? System.Text.RegularExpressions.Regex.IsMatch(text, @"\bfrei\b") : text.Contains(word));
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
