using System.Collections.Concurrent;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Caps failed Microsoft password submits per user (default 5 per 15 minutes). Only failures count; a success clears the count.</summary>
    public sealed class PasswordAttemptLimiter(TimeProvider time, int maxFailures = 5, int windowMinutes = 15)
    {
        private readonly ConcurrentDictionary<Guid, List<DateTimeOffset>> failures = new();
        private readonly TimeSpan window = TimeSpan.FromMinutes(windowMinutes);

        public bool IsBlocked(Guid userId)
        {
            if (!failures.TryGetValue(userId, out var list)) return false;
            lock (list)
            {
                Prune(list);
                return list.Count >= maxFailures;
            }
        }

        public void RecordFailure(Guid userId)
        {
            var list = failures.GetOrAdd(userId, _ => []);
            lock (list)
            {
                Prune(list);
                list.Add(time.GetUtcNow());
            }
        }

        public void Reset(Guid userId) => failures.TryRemove(userId, out _);

        private void Prune(List<DateTimeOffset> list)
        {
            var cutoff = time.GetUtcNow() - window;
            list.RemoveAll(t => t < cutoff);
        }
    }
}
