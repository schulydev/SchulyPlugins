using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Purge.Tests
{
    public sealed class FakeVault : IPluginVault
    {
        private readonly Dictionary<string, string> items = new();
        public IReadOnlyCollection<string> Keys => items.Keys;
        public void Set(string key, string value) => items[key] = value;
        public string? Get(string key) => items.GetValueOrDefault(key);
        public bool TryGet(string key, out string? value) => items.TryGetValue(key, out value);
        public bool Contains(string key) => items.ContainsKey(key);
        public bool Remove(string key) => items.Remove(key);
        public void Clear() => items.Clear();
        public int Count => items.Count;
    }

    public sealed class FakeSchoolData : ISchoolData
    {
        public int StudentsEnsured { get; private set; }
        public Task<Guid> EnsureSchoolAsync(SchoolRecord school, CancellationToken cancellationToken = default) => Task.FromResult(Guid.NewGuid());
        public Task<Guid> EnsureStudentAsync(Guid schoolId, Guid applicationUserId, StudentRecord student, CancellationToken cancellationToken = default)
        {
            StudentsEnsured++;
            return Task.FromResult(Guid.NewGuid());
        }
        public Task<SyncResult> SyncAgendaAsync(Guid schoolUserId, IReadOnlyCollection<AgendaRecord> entries, CancellationToken cancellationToken = default) => Task.FromResult(new SyncResult(0, 0));
        public Task<SyncResult> SyncGradesAsync(Guid schoolUserId, IReadOnlyCollection<GradeRecord> grades, CancellationToken cancellationToken = default) => Task.FromResult(new SyncResult(0, 0));
        public Task<SyncResult> SyncAbsencesAsync(Guid schoolUserId, IReadOnlyCollection<AbsenceRecord> absences, CancellationToken cancellationToken = default) => Task.FromResult(new SyncResult(0, 0));
        public Task<SyncResult> SyncDocumentsAsync(Guid schoolUserId, IReadOnlyCollection<DocumentRecord> documents, CancellationToken cancellationToken = default) => Task.FromResult(new SyncResult(0, 0));
        public Task<SyncResult> SyncSemesterReportsAsync(Guid schoolUserId, IReadOnlyCollection<SemesterReportRecord> reports, CancellationToken cancellationToken = default) => Task.FromResult(new SyncResult(0, 0));
        public Task<IReadOnlySet<string>> GetDocumentIdsAsync(Guid schoolUserId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlySet<string>>(new HashSet<string>());
    }

    public sealed class FakeFileStore : IPluginFileStore
    {
        public Task<StoredFile> SaveAsync(Stream content, string fileName, string? contentType, CancellationToken cancellationToken = default) => Task.FromResult(new StoredFile(Guid.NewGuid().ToString("N"), content.Length));
    }

    public sealed class NoLinkedAccounts : ILinkedAccounts
    {
        public Task<IReadOnlyList<LinkedAccount>> ListAsync(Guid applicationUserId, string provider, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LinkedAccount>>([]);
        public Task<LinkedAccount?> GetAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) => Task.FromResult<LinkedAccount?>(null);
        public Task<LinkedAccount> SaveAsync(Guid applicationUserId, LinkedAccountRecord account, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> GetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
        public Task SetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class FakeUserContext : IPluginUserContext
    {
        public Task<Guid> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException("No request in a background test.");
        public Task<Guid?> GetCurrentSchoolUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    public static class PluginServices
    {
        /// <summary>Everything the host hands a plugin, plus an in-memory stand-in for the plugin's own database.</summary>
        public static void AddHostServices<TContext>(this IServiceCollection services, FakeVault vault, FakeSchoolData schoolData) where TContext : DbContext
        {
            services.RemoveAll<DbContextOptions<TContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<TContext>>();
            var name = Guid.NewGuid().ToString();
            services.AddDbContext<TContext>(o => o.UseInMemoryDatabase(name));

            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
            services.AddHttpClient();
            services.AddSingleton<IHttpContextAccessor, HttpContextAccessor>();
            services.AddSingleton<IPluginVault>(vault);
            services.AddSingleton<ISchoolData>(schoolData);
            services.AddSingleton<IPluginFileStore, FakeFileStore>();
            services.AddSingleton<IPluginUserContext, FakeUserContext>();
            services.AddSingleton<ILinkedAccounts, NoLinkedAccounts>();
        }
    }
}
