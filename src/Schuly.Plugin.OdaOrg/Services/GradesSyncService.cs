using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Models;
using Schuly.Plugin.Shared;

namespace Schuly.Plugin.OdaOrg.Services
{
    public class GradesSyncService(ISchoolData schoolData, ILogger<GradesSyncService> logger)
    {
        public async Task SyncAsync(OdaOrgAccount account, IReadOnlyList<ModuleGrade> grades, CancellationToken ct)
        {
            if (account.SchoolUserId is null || grades.Count == 0) return;

            var records = grades
                .Select(g => new GradeRecord(ExternalIds.Of("module", g.ModuleName), g.ModuleName, g.ModuleName, g.FinalGrade))
                .ToList();

            var synced = await schoolData.SyncGradesAsync(account.SchoolUserId.Value, records, ct);
            if (synced.Added + synced.Updated > 0)
                logger.LogInformation("Synced OdaOrg grades for account {Account}: {Added} added, {Updated} updated", account.Id, synced.Added, synced.Updated);
        }
    }
}
