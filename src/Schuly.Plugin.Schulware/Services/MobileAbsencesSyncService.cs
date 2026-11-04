using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
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
            var notices = await NoticesAsync(client, ct);
            var entries = new List<AbsenceEntry>();

            foreach (var absence in absences)
            {
                if (!TryDate(absence.DateFrom, out var from) || !TryDate(absence.DateTo, out var to))
                    continue;

                var notice = absence.Id is not null && notices.TryGetValue(absence.Id, out var found) ? found : null;
                var start = AbsenceMerge.Time(absence.HourFrom);
                var end = AbsenceMerge.Time(absence.HourTo);
                entries.Add(new AbsenceEntry(from, to, Blank(absence.Reason) ?? Blank(absence.Category) ?? "Imported from Schulnetz")
                {
                    IsExcused = absence.IsExcused,
                    StartTime = start is not null && end is not null ? start : null,
                    EndTime = start is not null && end is not null ? end : null,
                    ParentStatus = Blank(absence.StatusEAE),
                    ParentStatusDate = AbsenceMerge.Date(absence.DateEAE),
                    TrainerStatus = Blank(absence.StatusEAB) ?? Blank(notice?.TrainerAcknowledgement),
                    TrainerStatusDate = AbsenceMerge.Date(absence.DateEAB) ?? AbsenceMerge.Date(notice?.TrainerCommentTimestamp),
                    TrainerComment = Blank(absence.CommentEAB) ?? Blank(notice?.TrainerComment),
                    Comment = Blank(absence.Comment),
                    Lessons = AbsenceMerge.Lessons(absence.Lessons),
                });
            }

            var records = AbsenceMerge.ToRecords(entries);
            LogStatusValues(entries);

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

        /// <summary>Which notice holds the training company's answer, per absence. Absences still sync when this list fails.</summary>
        private async Task<Dictionary<string, AbsenceNoticeDto>> NoticesAsync(SchulwareApiClient client, CancellationToken ct)
        {
            try
            {
                var rows = await client.Api.Mobile.Absencenotices.GetAsync(cancellationToken: ct) ?? [];
                return rows
                    .Where(n => n.AbsenceId is not null && (Blank(n.TrainerAcknowledgement) ?? Blank(n.TrainerComment)) is not null)
                    .GroupBy(n => n.AbsenceId!)
                    .ToDictionary(g => g.Key, g => g.First());
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Absence notices could not be read: {Error}", ex.GetType().Name);
                return [];
            }
        }

        /// <summary>The words each school uses for these steps haven't been seen yet. This shows them without any reason or comment: only short plain words get through.</summary>
        private void LogStatusValues(List<AbsenceEntry> entries)
        {
            var parents = Words(entries.Select(e => e.ParentStatus));
            var trainers = Words(entries.Select(e => e.TrainerStatus));
            if (parents.Count + trainers.Count > 0)
                logger.LogInformation("Absence status values: StatusEAE {Parents}, trainer {Trainers}", parents, trainers);
        }

        private static List<string> Words(IEnumerable<string?> values) => values
            .OfType<string>()
            .Select(v => v.Length <= 20 && v.All(c => char.IsLetter(c) || c == ' ') ? v : "(other)")
            .Distinct()
            .ToList();

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

        private static string? Blank(string? value) => AbsenceMerge.Blank(value);
    }
}
