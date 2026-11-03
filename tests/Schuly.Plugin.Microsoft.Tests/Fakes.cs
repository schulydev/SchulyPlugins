using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft.Tests
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

    public sealed class FakeLinkedAccounts : ILinkedAccounts
    {
        public readonly List<(Guid UserId, LinkedAccount Account)> Rows = new();

        public Guid Add(Guid userId, string provider = LinkedProviders.Microsoft)
        {
            var account = new LinkedAccount(Guid.NewGuid(), provider, Guid.NewGuid().ToString(), "me@school.ch", "Me", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
            Rows.Add((userId, account));
            return account.Id;
        }

        public Task<IReadOnlyList<LinkedAccount>> ListAsync(Guid applicationUserId, string provider, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LinkedAccount>>(Rows.Where(r => r.UserId == applicationUserId && r.Account.Provider == provider).Select(r => r.Account).ToList());

        public Task<LinkedAccount?> GetAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.Where(r => r.UserId == applicationUserId && r.Account.Id == linkedAccountId).Select(r => r.Account).FirstOrDefault());

        public Task<LinkedAccount> SaveAsync(Guid applicationUserId, LinkedAccountRecord account, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> RemoveAsync(Guid applicationUserId, Guid linkedAccountId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Rows.RemoveAll(r => r.UserId == applicationUserId && r.Account.Id == linkedAccountId) > 0);

        public Task<string?> GetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, CancellationToken cancellationToken = default) => Task.FromResult<string?>("refresh-token");

        public Task SetSecretAsync(Guid applicationUserId, Guid linkedAccountId, string name, string value, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    public sealed class FakeUser(Guid id) : IPluginUserContext
    {
        public Task<Guid> GetCurrentUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(id);
        public Task<Guid?> GetCurrentSchoolUserIdAsync(CancellationToken cancellationToken = default) => Task.FromResult<Guid?>(null);
    }

    /// <summary>SchulyMicrosoft stand-in: answers each upstream path (without query) from a table and records what was asked.</summary>
    public sealed class FakeService : HttpMessageHandler
    {
        public readonly Dictionary<string, string> Answers = new();
        public readonly List<(string Method, string PathAndQuery, string? Body)> Calls = new();
        /// <summary>Upstream failures by path, with an optional Retry-After in seconds.</summary>
        public readonly Dictionary<string, (HttpStatusCode Status, int? RetryAfter)> Failures = new();
        /// <summary>When set, answers wait for it, for a read that is still running.</summary>
        public TaskCompletionSource? Hold;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            lock (Calls)
                Calls.Add((request.Method.Method, Uri.UnescapeDataString(request.RequestUri.PathAndQuery), body));
            var key = path.StartsWith("/api/") ? path[5..] : path;
            if (Failures.TryGetValue(key, out var failure))
            {
                var failed = new HttpResponseMessage(failure.Status);
                if (failure.RetryAfter is { } seconds)
                    failed.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return failed;
            }
            if (!Answers.TryGetValue(key, out var json))
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            // The answer is taken before the wait, so a held read returns what was true when it started.
            if (Hold is { } hold)
                await hold.Task;
            if (json.StartsWith("bytes:"))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(json[6..])) { Headers = { ContentType = new("image/png") } } };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    public sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan by) => now += by;
    }

    public sealed class FakeFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, false);
    }

    /// <summary>One signed-in user with two linked Microsoft accounts, and everything a controller needs.</summary>
    public sealed class Harness
    {
        public readonly Guid User = Guid.NewGuid();
        public readonly Guid Account;
        public readonly Guid OtherAccount;
        public readonly FakeLinkedAccounts Accounts = new();
        public readonly FakeService Service = new();
        public readonly IdSealer Sealer = new(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        public readonly FakeVault Vault = new();
        private readonly IConfiguration config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MicrosoftApi:BaseUrl"] = "http://ms.test" }).Build();

        public Harness()
        {
            Account = Accounts.Add(User);
            OtherAccount = Accounts.Add(User);
        }

        public MicrosoftAccountReader Reader() => new(Accounts, new MicrosoftServiceClient(new FakeFactory(Service), config));

        public IdScope Scope(Guid? account = null, Guid? user = null) => new(Sealer, user ?? User, account ?? Account);

        public string Seal(string kind, string id, Guid? account = null, Guid? user = null) => Scope(account, user).Seal(kind, id);

        public T Controller<T>(Func<IPluginUserContext, ILinkedAccounts, MicrosoftAccountReader, T> create, Guid? asUser = null) where T : ControllerBase
        {
            var controller = create(new FakeUser(asUser ?? User), Accounts, Reader());
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };
            return controller;
        }

        public ScheduledMessageStore Scheduled() => new(Vault);

        public IConfiguration Config => config;

        /// <summary>The app's container as far as background refreshes need it: they read through their own scope.</summary>
        public IServiceScopeFactory Scopes() => new ServiceCollection()
            .AddScoped(_ => Reader())
            .BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        public readonly FakeClock Clock = new();

        public MicrosoftResponseCache ResponseCache => responseCache ??= new MicrosoftResponseCache(Scopes(), config, Clock);
        private MicrosoftResponseCache? responseCache;
    }
}
