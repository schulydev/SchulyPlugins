using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>One cached answer: the sealed JSON the live route would send, and how to tell if a browser already has it.</summary>
    public sealed record CachedResponse(byte[] Body, string ETag, DateTimeOffset At, DateTimeOffset GoneAt, TimeSpan Fresh, TimeSpan Stale);

    /// <summary>How long a kind of answer stays usable, after OutNote's docs/frontend-and-api.md. Each can be changed under <c>MicrosoftApi:Cache:Times:&lt;name&gt;</c> (<c>Fresh</c>, <c>Stale</c>, in seconds).</summary>
    public sealed record CacheTimes(string Name, int FreshSeconds, int StaleSeconds)
    {
        public static readonly CacheTimes Bootstrap = new(nameof(Bootstrap), 5, 600);
        public static readonly CacheTimes Lists = new(nameof(Lists), 30, 600);
        /// <summary>The messages of an open chat. The guide has no row for these; its list time would make a reply take up to a minute to show.</summary>
        public static readonly CacheTimes ChatMessages = new(nameof(ChatMessages), 10, 600);
        public static readonly CacheTimes Message = new(nameof(Message), 300, 3600);
        public static readonly CacheTimes Folders = new(nameof(Folders), 300, 86400);
    }

    /// <summary>
    /// The short-lived memory cache in front of SchulyMicrosoft, a C# port of OutNote's ApiCache (docs/frontend-and-api.md). Under <c>fresh</c> it answers from memory, under <c>stale</c> from memory
    /// with a refresh in the background, and older it waits for Microsoft and falls back to the old answer only if that fails for a passing reason. Identical requests share one call, and a generation
    /// per user stops a slow old read from overwriting a newer write. Entries hold sealed answers and are keyed by user and account, so one user's entry can't reach another. Nothing goes to disk and
    /// nothing is logged. Only <see cref="Controllers.CachedController"/> reads it, so Helply, MCP, connected AI apps and the mobile app stay live.
    /// </summary>
    public sealed class MicrosoftResponseCache(IServiceScopeFactory scopes, IConfiguration configuration, TimeProvider clock)
    {
        public const string BootstrapName = "bootstrap";
        public const string MailPrefix = "mail";
        public const string ChatsPrefix = "chats";

        private readonly record struct Key(Guid UserId, Guid AccountId, string Name);

        private readonly Lock gate = new();
        private readonly OrderedDictionary<Key, CachedResponse> entries = new();
        private readonly Dictionary<Key, Task<CachedResponse>> running = new();
        private readonly Dictionary<Guid, long> generations = new();
        private readonly Dictionary<(Guid UserId, Guid AccountId), DateTimeOffset> throttled = new();
        private readonly Dictionary<Guid, (DateTimeOffset Window, int Count)> calls = new();
        private long totalBytes;

        private int MaxEntries => Setting("MaxEntries", 5000);
        private long MaxBytes => Setting("MaxMegabytes", 256) * 1024L * 1024L;
        private int CallsPerMinute => Setting("CallsPerMinute", 120);
        /// <summary>How long past <c>stale</c> an answer waits as a fallback. After that it is gone, so the time data stays in memory has an end.</summary>
        private TimeSpan KeepOld => TimeSpan.FromSeconds(Setting("KeepOldSeconds", 3600));

        private int Setting(string name, int fallback) => int.TryParse(configuration[$"MicrosoftApi:Cache:{name}"], out var value) && value > 0 ? value : fallback;

        private TimeSpan Time(CacheTimes times, string part, int fallback) => TimeSpan.FromSeconds(int.TryParse(configuration[$"MicrosoftApi:Cache:Times:{times.Name}:{part}"], out var value) && value >= 0 ? value : fallback);

        /// <summary>The answer for this user's account and <paramref name="name"/>. <paramref name="fetch"/> runs in its own scope, because a background refresh outlives the request, and must return the sealed JSON.</summary>
        public async Task<CachedResponse> GetAsync(Guid userId, Guid accountId, string name, CacheTimes times, Func<IServiceProvider, Task<byte[]>> fetch, CancellationToken ct)
        {
            var key = new Key(userId, accountId, name);
            var fresh = Time(times, "Fresh", times.FreshSeconds);
            var stale = Time(times, "Stale", times.StaleSeconds);
            var now = clock.GetUtcNow();
            CachedResponse? entry;
            Task<CachedResponse> task;
            lock (gate)
            {
                entries.TryGetValue(key, out entry);
                if (entry is not null && entry.GoneAt <= now)
                {
                    Remove(key);
                    entry = null;
                }
                if (entry is not null && now - entry.At < fresh)
                {
                    Touch(key, entry);
                    return entry;
                }
                if (!running.TryGetValue(key, out var existing))
                {
                    // No new call to Microsoft while it asked to wait, or past this user's limit: what is held is the answer, if anything is.
                    var wait = WaitBeforeCall(userId, accountId, now);
                    if (wait is { } seconds)
                        return entry ?? throw new MicrosoftThrottledException(seconds);
                    existing = Start(key, fresh, stale, fetch);
                }
                task = existing;
            }
            if (entry is not null && now - entry.At < stale)
                return entry;
            try
            {
                return await task.WaitAsync(ct);
            }
            catch (Exception ex) when (entry is not null && !ct.IsCancellationRequested && IsPassing(ex))
            {
                return entry;
            }
        }

        /// <summary>Whole seconds since Microsoft gave this answer, for the <c>Age</c> header.</summary>
        public long AgeSeconds(CachedResponse entry) => (long)Math.Max(0, (clock.GetUtcNow() - entry.At).TotalSeconds);

        /// <summary>Forgets a user's answers for one account whose name starts with a prefix. Reads already running for the user can't store theirs any more.</summary>
        public void Invalidate(Guid userId, Guid accountId, string prefix)
        {
            lock (gate)
            {
                Forget(userId);
                foreach (var key in entries.Keys.Where(k => k.UserId == userId && k.AccountId == accountId && k.Name.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                    Remove(key);
            }
        }

        /// <summary>After a write to mail: Microsoft has the change, so nothing old may be answered.</summary>
        public void AfterMailWrite(Guid userId, Guid accountId)
        {
            Invalidate(userId, accountId, MailPrefix);
            Invalidate(userId, accountId, BootstrapName);
        }

        public void AfterChatWrite(Guid userId, Guid accountId)
        {
            Invalidate(userId, accountId, ChatsPrefix);
            Invalidate(userId, accountId, BootstrapName);
        }

        /// <summary>Forgets everything of one account: it was unlinked.</summary>
        public void ClearAccount(Guid userId, Guid accountId) => Invalidate(userId, accountId, "");

        /// <summary>Forgets everything of a user: sign-out or account deletion.</summary>
        public void ClearUser(Guid userId)
        {
            lock (gate)
            {
                Forget(userId);
                foreach (var key in entries.Keys.Where(k => k.UserId == userId).ToList())
                    Remove(key);
                calls.Remove(userId);
                foreach (var pair in throttled.Keys.Where(p => p.UserId == userId).ToList())
                    throttled.Remove(pair);
            }
        }

        /// <summary>Drops what has run out, so nothing stays in memory past its time when nobody asks for it again, and returns the accounts still held.</summary>
        public IReadOnlyCollection<(Guid UserId, Guid AccountId)> Sweep()
        {
            var now = clock.GetUtcNow();
            lock (gate)
            {
                foreach (var key in entries.Where(e => e.Value.GoneAt <= now).Select(e => e.Key).ToList())
                    Remove(key);
                foreach (var pair in throttled.Where(t => t.Value <= now).Select(t => t.Key).ToList())
                    throttled.Remove(pair);
                foreach (var user in calls.Where(c => now - c.Value.Window >= TimeSpan.FromMinutes(1)).Select(c => c.Key).ToList())
                    calls.Remove(user);
                return entries.Keys.Select(k => (k.UserId, k.AccountId)).Distinct().ToList();
            }
        }

        public int Count
        {
            get
            {
                lock (gate)
                    return entries.Count;
            }
        }

        private Task<CachedResponse> Start(Key key, TimeSpan fresh, TimeSpan stale, Func<IServiceProvider, Task<byte[]>> fetch)
        {
            generations.TryGetValue(key.UserId, out var generation);
            var task = Task.Run(() => LoadAsync(key, fresh, stale, fetch, generation));
            running[key] = task;
            // A refresh nobody waits for may fail; that must not surface as an unobserved exception.
            _ = task.ContinueWith(t => t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return task;
        }

        private async Task<CachedResponse> LoadAsync(Key key, TimeSpan fresh, TimeSpan stale, Func<IServiceProvider, Task<byte[]>> fetch, long generation)
        {
            try
            {
                byte[] body;
                await using (var scope = scopes.CreateAsyncScope())
                    body = await fetch(scope.ServiceProvider);
                var at = clock.GetUtcNow();
                var entry = new CachedResponse(body, $"\"{Convert.ToHexStringLower(SHA256.HashData(body))[..32]}\"", at, at + stale + KeepOld, fresh, stale);
                lock (gate)
                {
                    generations.TryGetValue(key.UserId, out var current);
                    if (current == generation)
                        Store(key, entry);
                }
                return entry;
            }
            catch (MicrosoftThrottledException ex)
            {
                lock (gate)
                    throttled[(key.UserId, key.AccountId)] = clock.GetUtcNow() + ex.RetryAfter;
                throw;
            }
            finally
            {
                lock (gate)
                {
                    generations.TryGetValue(key.UserId, out var current);
                    if (current == generation)
                        running.Remove(key);
                }
            }
        }

        private TimeSpan? WaitBeforeCall(Guid userId, Guid accountId, DateTimeOffset now)
        {
            if (throttled.TryGetValue((userId, accountId), out var until) && until > now)
                return until - now;
            calls.TryGetValue(userId, out var window);
            if (now - window.Window >= TimeSpan.FromMinutes(1))
                window = (now, 0);
            if (window.Count >= CallsPerMinute)
                return window.Window + TimeSpan.FromMinutes(1) - now;
            calls[userId] = (window.Window, window.Count + 1);
            return null;
        }

        private void Forget(Guid userId)
        {
            generations.TryGetValue(userId, out var generation);
            generations[userId] = generation + 1;
            foreach (var key in running.Keys.Where(k => k.UserId == userId).ToList())
                running.Remove(key);
        }

        private void Store(Key key, CachedResponse entry)
        {
            Remove(key);
            entries.Add(key, entry);
            totalBytes += entry.Body.Length;
            while (entries.Count > 1 && (entries.Count > MaxEntries || totalBytes > MaxBytes))
                Remove(entries.GetAt(0).Key);
        }

        private void Touch(Key key, CachedResponse entry)
        {
            entries.Remove(key);
            entries.Add(key, entry);
        }

        private void Remove(Key key)
        {
            if (entries.Remove(key, out var entry))
                totalBytes -= entry.Body.Length;
        }

        // A deleted message or an expired sign-in must show as such; only an outage or a limit is worth covering with something old.
        private static bool IsPassing(Exception ex) =>
            ex is TaskCanceledException or HttpRequestException { StatusCode: null or HttpStatusCode.TooManyRequests or >= HttpStatusCode.InternalServerError };
    }

    /// <summary>Clears out what ran out, and the accounts unlinked through core's own route, which doesn't reach the plugin (the routes already answer 404 for those).</summary>
    public sealed class MicrosoftResponseCacheSweepTask : IPluginBackgroundTask
    {
        public string Name => "Microsoft response cache sweep";
        public PluginSchedule Schedule => PluginSchedule.Every(TimeSpan.FromMinutes(5));

        public async Task ExecuteAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken)
        {
            using var scope = serviceProvider.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<MicrosoftResponseCache>();
            var linkedAccounts = scope.ServiceProvider.GetRequiredService<ILinkedAccounts>();
            foreach (var (userId, accountId) in cache.Sweep())
                if (await linkedAccounts.GetAsync(userId, accountId, cancellationToken) is null)
                    cache.ClearAccount(userId, accountId);
        }
    }
}
