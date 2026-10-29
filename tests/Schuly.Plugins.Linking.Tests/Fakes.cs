using System.Text.Json;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;

namespace Schuly.Plugins.Linking.Tests
{
    public sealed class FakeVault : IPluginVault
    {
        private readonly Dictionary<string, string> items = new();
        public void Set(string key, string value) => items[key] = value;
        public string? Get(string key) => items.GetValueOrDefault(key);
        public bool TryGet(string key, out string? value) => items.TryGetValue(key, out value);
        public bool Contains(string key) => items.ContainsKey(key);
        public bool Remove(string key) => items.Remove(key);
        public void Clear() => items.Clear();
        public int Count => items.Count;
    }

    /// <summary>Behaves like the host's store: SaveAsync matches on (user, provider, ExternalId).</summary>
    public sealed class FakeLinkedAccounts : ILinkedAccounts
    {
        public readonly List<(Guid UserId, LinkedAccount Account)> Rows = new();
        public readonly Dictionary<(Guid, string), string> Secrets = new();

        public Task<IReadOnlyList<LinkedAccount>> ListAsync(Guid applicationUserId, string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LinkedAccount>>(Rows.Where(r => r.UserId == applicationUserId && r.Account.Provider == provider).Select(r => r.Account).ToList());

        public Task<LinkedAccount?> GetAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.Where(r => r.UserId == applicationUserId && r.Account.Id == linkedAccountId).Select(r => r.Account).FirstOrDefault());

        public Task<LinkedAccount> SaveAsync(Guid applicationUserId, LinkedAccountRecord account, CancellationToken cancellationToken = default)
        {
            var index = Rows.FindIndex(r => r.UserId == applicationUserId && r.Account.Provider == account.Provider && r.Account.ExternalId == account.ExternalId);
            if (index >= 0)
            {
                var updated = Rows[index].Account with { Email = account.Email, DisplayName = account.DisplayName, UpdatedAt = DateTimeOffset.UtcNow };
                Rows[index] = (applicationUserId, updated);
                return Task.FromResult(updated);
            }
            var created = new LinkedAccount(Guid.NewGuid(), account.Provider, account.ExternalId, account.Email, account.DisplayName, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Rows.Add((applicationUserId, created));
            return Task.FromResult(created);
        }

        public Task<bool> RemoveAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.RemoveAll(r => r.UserId == applicationUserId && r.Account.Id == linkedAccountId) > 0);

        public Task<string?> GetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(Secrets.GetValueOrDefault((linkedAccountId, name)));

        public Task SetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, string value, CancellationToken cancellationToken = default)
        {
            Secrets[(linkedAccountId, name)] = value;
            return Task.CompletedTask;
        }
    }

    public sealed class FakeUser(Guid id) : IPluginUserContext
    {
        public Task<Guid> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id);
        public Task<Guid?> GetCurrentSchoolUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    /// <summary>Captures every log line (message and exception text) so tests can assert nothing secret was logged.</summary>
    public sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<string> Lines = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + " " + exception);
    }

    public static class StepAssert
    {
        /// <summary>Everything a client or the host could see or persist from a result, as text.</summary>
        public static string Dump(LinkStepResult result) => JsonSerializer.Serialize(result.Step) + "|" + result.State;

        public static LinkSubmitContext Submit(Guid user, string flow, LinkStepResult previous, Dictionary<string, string>? values = null, string? action = null, LinkPlatform platform = LinkPlatform.Web, string? reauth = null) =>
            new(user, flow, platform, previous.Step.Id, previous.State, values ?? new Dictionary<string, string>(), action, reauth, "https://host/callback");
    }
}
