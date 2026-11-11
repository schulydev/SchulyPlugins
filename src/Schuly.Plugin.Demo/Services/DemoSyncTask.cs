using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Demo.Services
{
    /// <summary>
    /// Writes the demo school into each account under <see cref="DemoAccounts.Key"/>, on startup and every hour, with
    /// dates built from now. The list is read on every run, so a changed plugin config applies on the next run.
    /// </summary>
    public class DemoSyncTask(TimeProvider time) : IPluginBackgroundTask
    {
        public string Name => "Demo School Sync";
        public PluginSchedule Schedule => PluginSchedule.Every(TimeSpan.FromHours(1)) with { RunOnStartup = true };

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            var logger = serviceProvider.GetRequiredService<ILogger<DemoSyncTask>>();
            var userIds = DemoAccounts.UserIds(serviceProvider.GetRequiredService<IConfiguration>());
            if (userIds.Count == 0)
            {
                logger.LogInformation("No demo accounts configured under {Key}; nothing to do", DemoAccounts.Key);
                return;
            }

            var snapshot = DemoSchool.Build(time.GetUtcNow());
            foreach (var userId in userIds)
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    await SyncAsync(scope.ServiceProvider.GetRequiredService<ISchoolData>(), scope.ServiceProvider.GetRequiredService<IPluginFileStore>(), userId, snapshot, cancellationToken);
                    logger.LogInformation("Synced demo data for user {UserId}", userId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    logger.LogError("Demo data for user {UserId} could not be synced ({ExceptionType})", userId, ex.GetType().Name);
                }
            }
        }

        public static async Task SyncAsync(ISchoolData schoolData, IPluginFileStore files, Guid userId, DemoSnapshot snapshot, CancellationToken cancellationToken)
        {
            var schoolId = await schoolData.EnsureSchoolAsync(snapshot.School, cancellationToken);
            var studentId = await schoolData.EnsureStudentAsync(schoolId, userId, snapshot.Student, cancellationToken);

            await schoolData.SyncGradesAsync(studentId, snapshot.Grades, cancellationToken);
            await schoolData.SyncExamsAsync(studentId, snapshot.Exams, cancellationToken);
            await schoolData.SyncAgendaAsync(studentId, snapshot.Agenda, cancellationToken);
            await schoolData.SyncAbsencesAsync(studentId, snapshot.Absences, cancellationToken);
            await schoolData.SyncSemesterReportsAsync(studentId, snapshot.Reports, cancellationToken);

            var stored = await schoolData.GetDocumentIdsAsync(studentId, cancellationToken);
            var documents = new List<DocumentRecord>();
            foreach (var doc in snapshot.Documents)
            {
                StoredFile? file = null;
                if (!stored.Contains(doc.ExternalId))
                {
                    using var content = new MemoryStream(DemoPdf.Render(doc.Title, doc.Lines));
                    file = await files.SaveAsync(content, doc.FileName, "application/pdf", cancellationToken);
                }
                documents.Add(new DocumentRecord(doc.ExternalId, doc.Title, doc.FileName, doc.Category, doc.Comment, DemoSchool.SchoolName, file));
            }
            await schoolData.SyncDocumentsAsync(studentId, documents, cancellationToken);
        }
    }
}
