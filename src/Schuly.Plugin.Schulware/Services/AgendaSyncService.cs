using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Syncs the timetable from Schulnetz's mobile API (<c>/me/events</c>), five weeks back to four months ahead.</summary>
    public class AgendaSyncService(ISchoolData schoolData, ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, IReadOnlyDictionary<string, string> subjectNames, CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            var events = await client.Api.Mobile.Events.GetAsync(config =>
            {
                config.QueryParameters.MinDate = today.AddDays(-35).ToString("yyyy-MM-dd");
                config.QueryParameters.MaxDate = today.AddDays(120).ToString("yyyy-MM-dd");
            }, ct);
            if (events is null || events.Count == 0) return;

            static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
            var records = new List<AgendaRecord>();
            foreach (var ev in events)
            {
                if (!string.Equals(ev.EventType, "Timetable", StringComparison.OrdinalIgnoreCase)) continue;
                if (!SwissTime.TryParseToUtc(ev.StartDate, out var start)) continue;

                string? mapped = null;
                if (Blank(ev.CourseToken) is { } token)
                    subjectNames.TryGetValue(token, out mapped);
                var title = Blank(ev.CourseName) ?? Blank(mapped) ?? Blank(ev.Text) ?? Blank(ev.CourseToken) ?? "Lektion";
                DateTimeOffset? end = SwissTime.TryParseToUtc(ev.EndDate, out var e) && e >= start ? e : null;
                var course = Blank(ev.CourseToken) ?? title;
                var teachers = ev.Teachers is { Count: > 0 } names ? string.Join(", ", names) : null;

                records.Add(new AgendaRecord(ExternalIds.Of("lesson", start.ToString("O"), course), AgendaKind.Lesson, title, start, end, Blank(ev.RoomToken), Blank(ev.Comment) ?? teachers));
            }

            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced agenda for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }
    }
}
