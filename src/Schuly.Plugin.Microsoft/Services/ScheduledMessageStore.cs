using System.Text.Json;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>A chat message waiting to be sent: only what the send needs, kept until it goes out (Schuly relays, it does not archive).</summary>
    public sealed record ScheduledMessage(Guid Id, Guid UserId, Guid AccountId, string ChatId, DateTimeOffset SendAt, string Content, string ContentType, string? Importance, int Attempts = 0);

    /// <summary>Scheduled chat messages in the plugin vault (encrypted at rest). An index entry per message plus one vault entry with its content; both are removed once it is sent or cancelled.</summary>
    public sealed class ScheduledMessageStore(IPluginVault vault)
    {
        public const int MaxPerAccount = 25;
        public static readonly TimeSpan MaxAhead = TimeSpan.FromDays(7);

        private const string IndexKey = "scheduled:index";
        private static readonly SemaphoreSlim Lock = new(1, 1);

        private sealed record IndexEntry(Guid Id, Guid UserId, Guid AccountId, DateTimeOffset SendAt);

        private static string ItemKey(Guid id) => $"scheduled:{id:N}";

        public async Task<ScheduledMessage?> AddAsync(ScheduledMessage message, CancellationToken ct)
        {
            await Lock.WaitAsync(ct);
            try
            {
                var index = ReadIndex();
                if (index.Count(e => e.UserId == message.UserId && e.AccountId == message.AccountId) >= MaxPerAccount)
                    return null;
                vault.Set(ItemKey(message.Id), JsonSerializer.Serialize(message));
                index.Add(new IndexEntry(message.Id, message.UserId, message.AccountId, message.SendAt));
                WriteIndex(index);
                return message;
            }
            finally
            {
                Lock.Release();
            }
        }

        public async Task<IReadOnlyList<ScheduledMessage>> ListAsync(Guid userId, Guid accountId, CancellationToken ct)
        {
            await Lock.WaitAsync(ct);
            try
            {
                return ReadIndex().Where(e => e.UserId == userId && e.AccountId == accountId).OrderBy(e => e.SendAt).Select(e => Read(e.Id)).OfType<ScheduledMessage>().ToList();
            }
            finally
            {
                Lock.Release();
            }
        }

        public async Task<bool> RemoveAsync(Guid userId, Guid accountId, Guid id, CancellationToken ct)
        {
            await Lock.WaitAsync(ct);
            try
            {
                var index = ReadIndex();
                if (index.RemoveAll(e => e.Id == id && e.UserId == userId && e.AccountId == accountId) == 0)
                    return false;
                vault.Remove(ItemKey(id));
                WriteIndex(index);
                return true;
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>Takes every message due by <paramref name="now"/> out of the store; the sender re-adds one only to retry it.</summary>
        public async Task<IReadOnlyList<ScheduledMessage>> TakeDueAsync(DateTimeOffset now, CancellationToken ct)
        {
            await Lock.WaitAsync(ct);
            try
            {
                var index = ReadIndex();
                var due = index.Where(e => e.SendAt <= now).ToList();
                if (due.Count == 0)
                    return [];
                var taken = due.Select(e => Read(e.Id)).OfType<ScheduledMessage>().ToList();
                foreach (var entry in due)
                    vault.Remove(ItemKey(entry.Id));
                index.RemoveAll(e => e.SendAt <= now);
                WriteIndex(index);
                return taken;
            }
            finally
            {
                Lock.Release();
            }
        }

        /// <summary>Drops every scheduled message of a user (account deletion).</summary>
        public async Task RemoveUserAsync(Guid userId, CancellationToken ct)
        {
            await Lock.WaitAsync(ct);
            try
            {
                var index = ReadIndex();
                foreach (var entry in index.Where(e => e.UserId == userId))
                    vault.Remove(ItemKey(entry.Id));
                index.RemoveAll(e => e.UserId == userId);
                WriteIndex(index);
            }
            finally
            {
                Lock.Release();
            }
        }

        private ScheduledMessage? Read(Guid id) =>
            vault.TryGet(ItemKey(id), out var json) ? JsonSerializer.Deserialize<ScheduledMessage>(json) : null;

        private List<IndexEntry> ReadIndex() =>
            vault.TryGet(IndexKey, out var json) ? JsonSerializer.Deserialize<List<IndexEntry>>(json) ?? [] : [];

        private void WriteIndex(List<IndexEntry> index)
        {
            if (index.Count == 0)
                vault.Remove(IndexKey);
            else
                vault.Set(IndexKey, JsonSerializer.Serialize(index));
        }
    }
}
