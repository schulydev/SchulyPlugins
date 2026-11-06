using System.Globalization;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.Schulware.Services
{
    public class AbsencesSyncService(ISchoolData schoolData, ILogger<AbsencesSyncService> logger)
    {
        public async Task SyncAsync(SchulwareApiClient client, SchulwareAccount account, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(account.WebSessionId))
            {
                logger.LogWarning("Account {AccountId} has no web session; skipping absence scrape", account.Id);
                return;
            }

            var result = await client.Api.Websession.Scrape.PostAsync(new WebScrapeRequestDto
            {
                Page = "absences",
                SessionId = account.WebSessionId,
                Id = account.WebSessionUserId,
                Transid = account.WebSessionTransId,
                UserAgent = account.UserAgent,
            }, cancellationToken: ct);

            var absences = result?.Absences?.Absences;
            if (absences is null || absences.Count == 0) return;

            var entries = new List<AbsenceEntry>();
            foreach (var absence in absences)
            {
                if (!TryParseDate(absence.DateFrom, out var from) || !TryParseDate(absence.DateTo, out var to))
                    continue;

                entries.Add(new AbsenceEntry(from, to, AbsenceMerge.Blank(absence.Reason) ?? "Imported from Schulnetz")
                {
                    IsExcused = absence.Excused,
                    ParentStatus = AbsenceMerge.Blank(absence.StatusEae),
                    TrainerComment = AbsenceMerge.Blank(absence.TrainerComment),
                    TrainerStatusDate = AbsenceMerge.Date(absence.AcknowledgedAt),
                    Comment = AbsenceMerge.Blank(absence.Comment),
                    ExcuseDeadline = AbsenceMerge.Date(absence.ExtensionDeadline),
                    Lessons = absence.Lessons,
                });
            }

            var records = AbsenceMerge.ToRecords(entries);
            var synced = await schoolData.SyncAbsencesAsync(account.SchoolUserId!.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced absences for account {AccountId}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }

        private static bool TryParseDate(string? raw, out DateOnly date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(raw)) return false;
            var cleaned = raw.Contains(',') ? raw[(raw.IndexOf(',') + 1)..].Trim() : raw.Trim();
            if (DateTime.TryParse(cleaned, CultureInfo.GetCultureInfo("de-CH"), DateTimeStyles.None, out var parsed)
                || DateTime.TryParse(cleaned, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            {
                date = DateOnly.FromDateTime(parsed);
                return true;
            }
            return false;
        }
    }
}
