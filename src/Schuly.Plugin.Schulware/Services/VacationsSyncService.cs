using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Schuly.Domain;
using Schuly.Domain.Enums;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Syncs a Schulware account's school holidays (Ferien) and public holidays
    /// into the main DB as Holiday-typed <see cref="AgendaEntry"/> rows (no class;
    /// scoped to the SchoolUser, with a Date..EndDate range).
    ///
    /// Schulnetz's Mobile <c>/me/vacations</c> endpoint is empty for this school;
    /// the holiday data instead arrives in the Mobile agenda as standalone,
    /// all-day, course-less entries with <c>eventType == "Event"</c>
    /// (e.g. "Sommerferien 2026", "Karfreitag 2026"). The regular
    /// <see cref="AgendaSyncService"/> drops those (it requires a class), so we
    /// pick them up here. Dedup is on (SchoolUserId, EntryType=Holiday, Date, Title).
    /// </summary>
    public class VacationsSyncService(Schuly.Infrastructure.SchulyDbContext mainDb, ILogger<VacationsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var schoolUserId = account.SchoolUserId!.Value;

            var events = await client.Api.Mobile.Agenda.GetAsync(cancellationToken: ct);
            if (events is null || events.Count == 0) return;

            var synced = 0;

            foreach (var e in events)
            {
                if (!string.Equals(e.EventType, "Event", StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrWhiteSpace(e.CourseName) || !string.IsNullOrWhiteSpace(e.CourseToken)) continue;
                if (!TryParseDate(e.StartDate, out var from) || from.TimeOfDay != TimeSpan.Zero) continue;

                var title = string.IsNullOrWhiteSpace(e.Text) ? "Ferien" : e.Text!;
                DateTime? to = TryParseDate(e.EndDate, out var end) && end >= from
                    ? end.Date == from.Date ? null : end
                    : null;

                var exists = await mainDb.AgendaEntries.AnyAsync(
                    a => a.SchoolUserId == schoolUserId
                         && a.EntryType == AgendaEntryType.Holiday
                         && a.Date == from
                         && a.Title == title, ct);
                if (exists) continue;

                mainDb.AgendaEntries.Add(new AgendaEntry
                {
                    EntryType = AgendaEntryType.Holiday,
                    Title = title,
                    Date = from,
                    EndDate = to,
                    SchoolUserId = schoolUserId,
                });
                synced++;
            }

            if (synced > 0)
            {
                await mainDb.SaveChangesAsync(ct);
                logger.LogInformation("Synced {Count} holidays for account {AccountId}", synced, account.Id);
            }
        }

        private static bool TryParseDate(string? raw, out DateTime date)
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
