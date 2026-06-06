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
    /// Syncs a Schulware account's agenda (lessons/tests/exams) into the main DB
    /// via the Mobile agenda endpoint. Dedup is on (ClassId, Date, Title); events
    /// without a resolvable class are skipped. (Agenda stays on the Mobile API
    /// even when the account has a web session — that session is reserved for the
    /// scraper-only document pages.)
    /// </summary>
    public class AgendaSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AgendaSyncService> logger)
    {
        private record AgendaRow(string? Start, string? Title, string? CourseName, string? CourseToken,
                                 string? Description, string? Place, string? Type);

        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var rows = await FromMobileAsync(client, ct);
            if (rows.Count == 0) return;

            var schoolUser = await mainDb.SchoolUsers
                .Include(su => su.Classes)
                .FirstOrDefaultAsync(su => su.Id == account.SchoolUserId!.Value, ct);
            if (schoolUser is null) return;

            var classesByName = schoolUser.Classes.ToDictionary(c => c.Name, c => c);
            var synced = 0;

            foreach (var ev in rows)
            {
                if (!TryParseStart(ev.Start, out var date)) continue;

                var title = ev.Title ?? ev.CourseName ?? ev.CourseToken ?? "Untitled";
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
                    Description = ev.Description,
                    Place = ev.Place,
                    EntryType = MapType(ev.Type),
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} agenda entries for account {AccountId}", synced, account.Id);
            }
        }

        private async Task<List<AgendaRow>> FromMobileAsync(SchulwareApiClient client, CancellationToken ct)
        {
            var events = await client.Api.Mobile.Agenda.GetAsync(cancellationToken: ct);
            return events?.Select(e => new AgendaRow(e.StartDate, e.Text, e.CourseName, e.CourseToken,
                                                     e.Comment, e.RoomToken, e.EventType)).ToList() ?? [];
        }

        private static Class? ResolveClass(AgendaRow ev, IReadOnlyDictionary<string, Class> classesByName)
        {
            if (!string.IsNullOrWhiteSpace(ev.CourseName) && classesByName.TryGetValue(ev.CourseName, out var byName))
                return byName;
            if (!string.IsNullOrWhiteSpace(ev.CourseToken) && classesByName.TryGetValue(ev.CourseToken, out var byToken))
                return byToken;
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
