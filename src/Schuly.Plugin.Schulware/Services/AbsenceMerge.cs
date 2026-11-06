using System.Globalization;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>One row as a Schulnetz page or the mobile API lists it.</summary>
    public sealed record AbsenceEntry(DateOnly From, DateOnly To, string Reason)
    {
        public bool? IsExcused { get; init; }
        public string? StartTime { get; init; }
        public string? EndTime { get; init; }
        public string? ParentStatus { get; init; }
        public DateOnly? ParentStatusDate { get; init; }
        public string? TrainerStatus { get; init; }
        public DateOnly? TrainerStatusDate { get; init; }
        public string? TrainerComment { get; init; }
        public string? Comment { get; init; }
        public DateOnly? ExcuseDeadline { get; init; }
        public int? Lessons { get; init; }
    }

    /// <summary>Schulnetz lists an absence once per class (a BM student's day off is one row for BM and one for the apprenticeship), so rows with the same dates and reason are one absence. The first reason of a date range keeps the plain id, so the web and the mobile sync update the same record.</summary>
    public static class AbsenceMerge
    {
        public static List<AbsenceRecord> ToRecords(IEnumerable<AbsenceEntry> entries)
        {
            var records = new List<AbsenceRecord>();
            foreach (var range in entries.GroupBy(e => (e.From, e.To)))
            {
                var first = true;
                foreach (var group in range.GroupBy(e => e.Reason).OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var (from, to) = range.Key;
                    var id = first ? ExternalIds.Of("absence", Day(from), Day(to)) : ExternalIds.Of("absence", Day(from), Day(to), group.Key);
                    records.Add(Merge(id, from, to, group.Key, group.ToList()));
                    first = false;
                }
            }
            return records;
        }

        private static AbsenceRecord Merge(string id, DateOnly from, DateOnly to, string reason, List<AbsenceEntry> rows)
        {
            var timed = rows.All(r => r.StartTime is not null && r.EndTime is not null);
            return new AbsenceRecord(id, SwissTime.UtcDate(from), SwissTime.UtcDate(to), reason)
            {
                // Excused only when every class says so: one open class still needs the excuse.
                IsExcused = rows.Any(r => r.IsExcused == false) ? false : rows.Any(r => r.IsExcused == true) ? true : null,
                StartTime = timed ? rows.Min(r => r.StartTime) : null,
                EndTime = timed ? rows.Max(r => r.EndTime) : null,
                ParentStatus = LeastDone(rows.Select(r => r.ParentStatus)),
                ParentStatusDate = rows.Max(r => r.ParentStatusDate),
                TrainerStatus = LeastDone(rows.Select(r => r.TrainerStatus)),
                TrainerStatusDate = rows.Max(r => r.TrainerStatusDate),
                TrainerComment = rows.Select(r => r.TrainerComment).FirstOrDefault(c => c is not null),
                Comment = rows.Select(r => r.Comment).FirstOrDefault(c => c is not null),
                ExcuseDeadline = rows.Min(r => r.ExcuseDeadline),
                Lessons = rows.Any(r => r.Lessons is not null) ? rows.Sum(r => r.Lessons ?? 0) : null,
            };
        }

        /// <summary>When classes differ, what's still open is what the student has to know.</summary>
        private static string? LeastDone(IEnumerable<string?> values)
        {
            var known = values.OfType<string>().ToList();
            return known.FirstOrDefault(v => string.Equals(v, "Offen", StringComparison.OrdinalIgnoreCase)) ?? known.FirstOrDefault();
        }

        private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        public static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>HH:mm, or null for a blank or a whole-day 00:00.</summary>
        public static string? Time(string? raw)
        {
            if (!TimeOnly.TryParse(raw, CultureInfo.InvariantCulture, out var time) || time == TimeOnly.MinValue)
                return null;
            return time.ToString("HH:mm", CultureInfo.InvariantCulture);
        }

        public static DateOnly? Date(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            var text = raw.Contains(',') ? raw[(raw.IndexOf(',') + 1)..].Trim() : raw.Trim();
            if (DateOnly.TryParseExact(text.Length > 10 ? text[..10] : text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
                return iso;
            return DateOnly.TryParseExact(text.Length > 10 ? text[..10] : text, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var swiss) ? swiss : null;
        }

        public static int? Lessons(string? raw) => int.TryParse(raw?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var lessons) ? lessons : null;
    }
}
