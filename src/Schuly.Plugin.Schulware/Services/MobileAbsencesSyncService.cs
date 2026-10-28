using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Absences (with their excused state) and lateness from the mobile API.</summary>
    public class MobileAbsencesSyncService(ISchoolData schoolData, ILogger<MobileAbsencesSyncService> logger)
    {
        public async Task<int> SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var absences = await client.Api.Mobile.Absences.GetAsync(cancellationToken: ct) ?? [];
            var lateness = await client.Api.Mobile.Lateness.GetAsync(cancellationToken: ct) ?? [];
            var records = new List<AbsenceRecord>();
            var usedIds = new HashSet<string>();

            foreach (var absence in absences.OrderBy(a => a.Id, StringComparer.Ordinal))
            {
                if (!TryDate(absence.DateFrom, out var from) || !TryDate(absence.DateTo, out var to))
                    continue;

                var id = ExternalIds.Of("absence", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"));
                if (!usedIds.Add(id))
                    id = ExternalIds.Of("absence", from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), absence.Id);
                var reason = Blank(absence.Reason) ?? Blank(absence.Category) ?? "Imported from Schulnetz";
                records.Add(new AbsenceRecord(id, SwissTime.UtcDate(from), SwissTime.UtcDate(to), reason) { IsExcused = absence.IsExcused });
            }

            foreach (var late in lateness)
            {
                if (!TryDate(late.Date, out var day) || !TryTime(day, late.StartTime, out var start))
                    continue;

                var end = TryTime(day, late.EndTime, out var e) && e >= start ? e : start;
                var reason = Blank(late.Reason) ?? "Late";
                records.Add(new AbsenceRecord(ExternalIds.Of("lateness", late.Id ?? $"{day:yyyy-MM-dd} {late.StartTime}"), start, end, reason, IsLate: true) { IsExcused = late.Excused });
            }

            var synced = await schoolData.SyncAbsencesAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced absences for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
            return records.Count;
        }

        private static bool TryDate(string? raw, out DateOnly date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var text = raw.Trim();
            return DateOnly.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                || DateOnly.TryParseExact(text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
        }

        private static bool TryTime(DateOnly day, string? raw, out DateTimeOffset utc)
        {
            utc = default;
            return TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, out var time) && SwissTime.TryToUtc(day.ToDateTime(time), out utc);
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
