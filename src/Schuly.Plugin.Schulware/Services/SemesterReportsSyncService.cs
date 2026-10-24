using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Syncs semester reports (Semesterzeugnisse) from Schulnetz's mobile API (<c>/me/certificates/grades</c>), one row per subject.</summary>
    public partial class SemesterReportsSyncService(ISchoolData schoolData, ILogger<SemesterReportsSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            var rows = await client.Api.Mobile.Certificates.GetAsync(cancellationToken: ct);
            if (rows is null || rows.Count == 0) return;

            var reports = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.CertificateId))
                .GroupBy(r => r.CertificateId!)
                .Select(ToReport)
                .OfType<SemesterReportRecord>()
                .ToList();

            var synced = await schoolData.SyncSemesterReportsAsync(account.SchoolUserId!.Value, reports, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced semester reports for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }

        internal static SemesterReportRecord? ToReport(IEnumerable<CertificateGradeDto> certificate)
        {
            var rows = certificate.ToList();
            var first = rows[0];
            if (string.IsNullOrWhiteSpace(first.Profile) || !TryParseSemester(first.SemesterTitle, out var yearStart, out var half))
                return null;

            var subjects = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Subject))
                .Select(ToSubject)
                .ToList();
            return new SemesterReportRecord(first.Profile!, yearStart, half, first.Class ?? "", subjects, first.PromotionDecision, ParseDecimal(first.AverageGrade), first.AbsenceExcused, first.AbsenceUnexcused);
        }

        /// <summary>"1. 25/26" is the first half of the school year starting 2025.</summary>
        internal static bool TryParseSemester(string? title, out int yearStart, out int half)
        {
            yearStart = half = 0;
            var match = SemesterTitle().Match(title ?? "");
            if (!match.Success) return false;
            half = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            yearStart = 2000 + int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            return half is 1 or 2;
        }

        /// <summary>"D (r) - Deutsch" is code D, type marker r, name Deutsch (the label wins when present).</summary>
        internal static SemesterSubjectRecord ToSubject(CertificateGradeDto row)
        {
            var match = SubjectText().Match(row.Subject!.Trim());
            var code = match.Success ? match.Groups["code"].Value.Trim() : row.Subject!.Trim();
            var type = match.Success && match.Groups["type"].Success ? match.Groups["type"].Value : null;
            var name = !string.IsNullOrWhiteSpace(row.Label) ? row.Label! : match.Success && match.Groups["name"].Success ? match.Groups["name"].Value.Trim() : code;
            var grade = ParseDecimal(row.Grade);
            return new SemesterSubjectRecord(code, name, grade, grade is null ? row.Grade : null, type);
        }

        private static decimal? ParseDecimal(string? value) =>
            decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;

        [GeneratedRegex(@"^\s*([12])\.\s*(\d{2})\s*/\s*\d{2}")]
        private static partial Regex SemesterTitle();

        [GeneratedRegex(@"^(?<code>[^(\-]+?)\s*(?:\((?<type>[^)]*)\))?\s*(?:-\s*(?<name>.+))?$")]
        private static partial Regex SubjectText();
    }
}
