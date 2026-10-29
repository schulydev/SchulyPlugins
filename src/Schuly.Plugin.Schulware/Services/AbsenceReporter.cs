using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Reports, lists and withdraws absences in Schulnetz. Shared by the account and stateless endpoints.</summary>
    public partial class AbsenceReporter(SchulnetzAbsenceApi api)
    {
        private const int MaxTextLength = 500;
        private const string OpenStatus = "open";

        [GeneratedRegex(@"for profile\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
        private static partial Regex ProfilePattern();

        public async Task<IReadOnlyList<ReportedAbsenceDto>> ListReportedAsync(CancellationToken ct)
        {
            var rows = await api.GetAbsencesAsync(ct);
            return rows
                .Where(r => IsOpen(r) && r.DateFrom is not null && r.DateTo is not null)
                .GroupBy(r => (From: Day(r.DateFrom), To: Day(r.DateTo), HourFrom: Hour(r.HourFrom), HourTo: Hour(r.HourTo), Reason: Blank(r.Reason), Comment: Blank(r.Comment)))
                .Select(g => new ReportedAbsenceDto(
                    g.Select(r => r.Id).ToList(),
                    g.Key.From!, g.Key.To!, g.Key.HourFrom, g.Key.HourTo, g.Key.Reason, g.Key.Comment,
                    g.Select(r => Blank(r.Profile)).OfType<string>().Distinct().ToList(),
                    g.Select(r => ReportedAt(r.StudentTimestamp)).OfType<string>().OrderBy(t => t, StringComparer.Ordinal).FirstOrDefault()))
                .OrderBy(a => a.DateFrom, StringComparer.Ordinal)
                .ThenBy(a => a.HourFrom, StringComparer.Ordinal)
                .ToList();
        }

        /// <summary>The configured reasons plus those the student used before. A failed Schulnetz read still returns the configured list; an expired sign-in propagates so the caller can refresh and retry.</summary>
        public async Task<IReadOnlyList<string>> ListReasonsAsync(IReadOnlyList<string> configured, ILogger logger, CancellationToken ct)
        {
            try
            {
                return AbsenceReasons.Merge(configured, await api.GetAbsencesAsync(ct));
            }
            catch (AbsenceReportException ex) when (ex.Code != "schulnetz_signin_expired")
            {
                logger.LogWarning("Could not read used absence reasons from Schulnetz ({Code}); returning the configured list only", ex.Code);
                return AbsenceReasons.Merge(configured, []);
            }
        }

        public async Task<AbsenceReportResult> CreateAsync(AbsenceReportRequest request, CancellationToken ct)
        {
            var item = Validate(request);
            var student = await api.GetStudentAsync(ct);
            if (!student.CanReportAbsences) throw AbsenceReportException.NotAllowed();

            var payload = new Dictionary<string, string> { ["studentId"] = student.Id, ["dateFrom"] = item.DateFrom!, ["dateTo"] = item.DateTo! };
            if (item.HourFrom is not null) payload["hourFrom"] = item.HourFrom;
            if (item.HourTo is not null) payload["hourTo"] = item.HourTo;
            if (item.Reason is not null) payload["reason"] = item.Reason;
            if (item.Comment is not null) payload["comment"] = item.Comment;

            var results = await api.CreateAsync([payload], ct);
            var failed = results.FirstOrDefault(r => r.Status != "0");
            if (failed is not null || results.Count == 0)
            {
                await RollBackAsync(results.Where(r => r.Status == "0" && r.Id is not null), ct);
                throw AbsenceReportException.Rejected(failed?.Message);
            }

            return new AbsenceReportResult(results.Select(r => new CreatedAbsenceDto(r.Id ?? "", ProfileOf(r.Message))).ToList());
        }

        public async Task DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
        {
            if (ids.Count == 0) throw AbsenceReportException.Invalid("ids is required");

            var open = (await api.GetAbsencesAsync(ct)).Where(IsOpen).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            if (ids.Any(id => !open.Contains(id))) throw AbsenceReportException.NotWithdrawable();

            foreach (var id in ids)
            {
                var result = await api.DeleteAsync(id, ct);
                if (result.Status != "0") throw AbsenceReportException.Rejected(result.Message);
            }
        }

        /// <summary>Parses the <c>ids</c> query value (<c>a,b</c>; repeated keys also work) into distinct ids.</summary>
        public static List<string> ParseIds(IEnumerable<string?> raw) =>
            raw.SelectMany(v => (v ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)).Distinct(StringComparer.Ordinal).ToList();

        internal static AbsenceReportRequest Validate(AbsenceReportRequest request)
        {
            if (!DateOnly.TryParseExact(request.DateFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from))
                throw AbsenceReportException.Invalid("dateFrom must be yyyy-MM-dd");
            if (!DateOnly.TryParseExact(request.DateTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to))
                throw AbsenceReportException.Invalid("dateTo must be yyyy-MM-dd");
            if (to < from) throw AbsenceReportException.Invalid("dateTo must not be before dateFrom");

            var hourFrom = Blank(request.HourFrom);
            var hourTo = Blank(request.HourTo);
            if ((hourFrom is null) != (hourTo is null)) throw AbsenceReportException.Invalid("hourFrom and hourTo must be given together");
            if (hourFrom is not null && hourTo is not null)
            {
                if (!TimeOnly.TryParseExact(hourFrom, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start))
                    throw AbsenceReportException.Invalid("hourFrom must be HH:mm");
                if (!TimeOnly.TryParseExact(hourTo, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
                    throw AbsenceReportException.Invalid("hourTo must be HH:mm");
                if (from == to && end <= start) throw AbsenceReportException.Invalid("hourTo must be after hourFrom");
            }

            var reason = Blank(request.Reason);
            var comment = Blank(request.Comment);
            if (reason?.Length > MaxTextLength) throw AbsenceReportException.Invalid($"reason must be at most {MaxTextLength} characters");
            if (comment?.Length > MaxTextLength) throw AbsenceReportException.Invalid($"comment must be at most {MaxTextLength} characters");

            return new AbsenceReportRequest(from.ToString("yyyy-MM-dd"), to.ToString("yyyy-MM-dd"), hourFrom, hourTo, reason, comment);
        }

        private async Task RollBackAsync(IEnumerable<AbsenceWriteResult> created, CancellationToken ct)
        {
            foreach (var result in created)
            {
                try { await api.DeleteAsync(result.Id!, ct); }
                catch (AbsenceReportException) { }
            }
        }

        /// <summary>Still unprocessed and reported by the student (has a student timestamp), so an absence a teacher recorded is never listed or withdrawable.</summary>
        private static bool IsOpen(AbsenceRow row) => string.Equals(row.Status, OpenStatus, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.StudentTimestamp);

        private static string? ProfileOf(string? message) => message is not null && ProfilePattern().Match(message) is { Success: true } m ? m.Groups[1].Value : null;

        private static string? Day(string? raw) => raw is { Length: >= 10 } ? raw[..10] : raw;

        private static string? Hour(string? raw) => Blank(raw) is { } text ? (text.Length >= 5 ? text[..5] : text) : null;

        private static string? ReportedAt(string? raw) =>
            DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) : null;

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
