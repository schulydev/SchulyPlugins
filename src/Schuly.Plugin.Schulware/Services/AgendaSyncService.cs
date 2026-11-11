using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Syncs the timetable from Schulnetz's mobile API (<c>/me/events</c>), five weeks back to four months ahead.</summary>
    public class AgendaSyncService(ISchoolData schoolData, ILogger<AgendaSyncService> logger)
    {
        public async Task<int> SyncAsync(SchulwareApiClient client, SchulwareAccount account, IReadOnlyDictionary<string, string> subjectNames, IReadOnlySet<string> gradedExamIds, CancellationToken ct)
        {
            var today = DateTime.UtcNow.Date;
            var events = await client.Api.Mobile.Events.GetAsync(config =>
            {
                config.QueryParameters.MinDate = today.AddDays(-35).ToString("yyyy-MM-dd");
                config.QueryParameters.MaxDate = today.AddDays(120).ToString("yyyy-MM-dd");
            }, ct) ?? [];

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

            var exams = await client.Api.Mobile.Exams.GetAsync(cancellationToken: ct) ?? [];
            foreach (var exam in exams)
            {
                if (Blank(exam.Id) is not { } examId || gradedExamIds.Contains(examId)) continue;
                if (!SwissTime.TryParseToUtc(exam.StartDate, out var start) || start < today.AddDays(-35)) continue;

                string? mapped = null;
                if (Blank(exam.CourseToken) is { } token)
                    subjectNames.TryGetValue(token, out mapped);
                var subject = Blank(exam.CourseName) ?? Blank(mapped) ?? Blank(exam.CourseToken) ?? "Prüfung";
                var title = Blank(exam.Text) is { } text ? $"{subject}: {text}" : $"Prüfung {subject}";
                DateTimeOffset? end = SwissTime.TryParseToUtc(exam.EndDate, out var e) && e >= start ? e : null;
                records.Add(new AgendaRecord(ExternalIds.Of("exam", examId), AgendaKind.Test, title, start, end, Blank(exam.RoomToken), Blank(exam.Comment)));
            }

            return await SaveAsync(account, records, ct);
        }

        /// <summary>Syncs the timetable from the Schulnetz website's scheduler, for schools that refuse the mobile app.</summary>
        public async Task<int> SyncWebAsync(SchulwareApiClient client, SchulwareAccount account, IReadOnlyDictionary<string, string> subjectNames, CancellationToken ct)
        {
            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "schedule",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);
            if (result?.Success != true)
                throw new WebSessionExpiredException(result?.Message);
            return await SaveAsync(account, FromWeb(result.Schedule ?? [], subjectNames), ct);
        }

        /// <summary>Scheduler events to agenda records: "*_stp" are lessons, "*_pru" exams; other kinds are skipped.</summary>
        public static List<AgendaRecord> FromWeb(IEnumerable<ScheduleEventDto> events, IReadOnlyDictionary<string, string> subjectNames)
        {
            static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
            var records = new List<AgendaRecord>();
            foreach (var ev in events)
            {
                if (!SwissTime.TryParseToUtc(ev.StartDate, out var start)) continue;
                DateTimeOffset? end = SwissTime.TryParseToUtc(ev.EndDate, out var e) && e >= start ? e : null;
                var token = Blank(ev.Kurskuerzel);
                string? mapped = null;
                if (token is not null)
                    subjectNames.TryGetValue(token, out mapped);
                if (ev.EventType?.EndsWith("_stp", StringComparison.OrdinalIgnoreCase) == true)
                {
                    var title = Blank(mapped) ?? token ?? Blank(ev.Text) ?? "Lektion";
                    // Same id as the mobile sync, so a school switching between the two keeps its lessons.
                    records.Add(new AgendaRecord(ExternalIds.Of("lesson", start.ToString("O"), token ?? title), AgendaKind.Lesson, title, start, end, Blank(ev.Zimmer), Blank(ev.Kommentar) ?? Blank(ev.Lehrerkuerzelname)));
                }
                else if (ev.EventType?.EndsWith("_pru", StringComparison.OrdinalIgnoreCase) == true && Blank(ev.Text) is { } text)
                {
                    records.Add(new AgendaRecord(ExternalIds.Of("exam", "web", start.ToString("O"), text), AgendaKind.Test, $"Prüfung: {text}", start, end, Blank(ev.Zimmer), Blank(ev.Kommentar)));
                }
            }
            return records;
        }

        private async Task<int> SaveAsync(SchulwareAccount account, List<AgendaRecord> records, CancellationToken ct)
        {
            if (records.Count == 0) return 0;
            var synced = await schoolData.SyncAgendaAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced agenda for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
            return records.Count;
        }
    }
}
