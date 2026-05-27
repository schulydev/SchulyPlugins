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
    /// Pulls a Schulware account's agenda (Schulnetz "events": lessons, tests,
    /// exams) and inserts new <see cref="AgendaEntry"/> rows in the main DB.
    /// Dedup is on (ClassId, Date, Title). Events without a resolvable class
    /// are skipped — <c>AgendaEntry.ClassId</c> is required.
    /// </summary>
    public class AgendaSyncService(
        Schuly.Infrastructure.SchulyDbContext mainDb,
        ILogger<AgendaSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var events = await client.Api.Mobile.Agenda.GetAsync(cancellationToken: ct);
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

                var title = ev.Text ?? ev.CourseName ?? "Untitled";
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
                    Description = ev.Comment,
                    Place = ev.RoomToken,
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

        private static Class? ResolveClass(EventDto ev, IReadOnlyDictionary<string, Class> classesByName)
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
