using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Demo.Tests
{
    public sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>Records what the plugin writes, keyed like the host: one school user per (school, user).</summary>
    public sealed class FakeSchoolData : ISchoolData
    {
        public List<Guid> Students { get; } = [];
        public Dictionary<string, object> Synced { get; } = [];
        public HashSet<string> DocumentIds { get; } = [];

        public Task<Guid> EnsureSchoolAsync(SchoolRecord school, CancellationToken cancellationToken = default) => Task.FromResult(Guid.Parse("11111111-1111-1111-1111-111111111111"));

        public Task<Guid> EnsureStudentAsync(Guid schoolId, Guid applicationUserId, StudentRecord student, CancellationToken cancellationToken = default)
        {
            Students.Add(applicationUserId);
            return Task.FromResult(applicationUserId);
        }

        public Task<SyncResult> SyncAgendaAsync(Guid schoolUserId, IReadOnlyCollection<AgendaRecord> entries, CancellationToken cancellationToken = default) => Record("agenda", entries);
        public Task<SyncResult> SyncGradesAsync(Guid schoolUserId, IReadOnlyCollection<GradeRecord> grades, CancellationToken cancellationToken = default) => Record("grades", grades);
        public Task<SyncResult> SyncExamsAsync(Guid schoolUserId, IReadOnlyCollection<ExamRecord> exams, CancellationToken cancellationToken = default) => Record("exams", exams);
        public Task<SyncResult> SyncAbsencesAsync(Guid schoolUserId, IReadOnlyCollection<AbsenceRecord> absences, CancellationToken cancellationToken = default) => Record("absences", absences);
        public Task<SyncResult> SyncDocumentsAsync(Guid schoolUserId, IReadOnlyCollection<DocumentRecord> documents, CancellationToken cancellationToken = default)
        {
            foreach (var d in documents.Where(d => d.File is not null))
                DocumentIds.Add(d.ExternalId);
            return Record("documents", documents);
        }
        public Task<SyncResult> SyncSemesterReportsAsync(Guid schoolUserId, IReadOnlyCollection<SemesterReportRecord> reports, CancellationToken cancellationToken = default) => Record("reports", reports);
        public Task<IReadOnlySet<string>> GetDocumentIdsAsync(Guid schoolUserId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<string>>(DocumentIds.ToHashSet());

        private Task<SyncResult> Record(string kind, object records)
        {
            Synced[kind] = records;
            return Task.FromResult(new SyncResult(0, 0));
        }
    }

    public sealed class FakeFileStore : IPluginFileStore
    {
        public List<(string FileName, byte[] Content)> Saved { get; } = [];

        public Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default)
        {
            using var copy = new MemoryStream();
            content.CopyTo(copy);
            Saved.Add((fileName, copy.ToArray()));
            return Task.FromResult(new StoredFile($"key-{Saved.Count}", copy.Length));
        }
    }
}
