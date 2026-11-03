using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    /// <summary>The memory cache and its routes against a fake SchulyMicrosoft, with a fake clock.</summary>
    public class ResponseCacheTests
    {
        private const string MailOne = "AAMkRAWMAILONE=";
        private static readonly string List = "[{\"id\":\"" + MailOne + "\",\"subject\":\"s\"}]";

        private static Harness Start()
        {
            var h = new Harness();
            h.Service.Answers["mail"] = List;
            h.Service.Answers["mail/folders"] = """[{"id":"AAMkFOLDER=","displayName":"Inbox"}]""";
            h.Service.Answers["chats"] = """[{"id":"19:c@thread.v2","chatType":"group"}]""";
            h.Service.Answers[$"mail/{MailOne}"] = $$$"""{"id":"{{{MailOne}}}","body":{"content":"secret"}}""";
            return h;
        }

        private static CachedController Cached(Harness h, Guid? asUser = null, string? ifNoneMatch = null)
        {
            var controller = h.Controller((u, l, r) => new CachedController(u, l, r, h.Sealer, h.ResponseCache), asUser);
            if (ifNoneMatch is not null)
                controller.Request.Headers.IfNoneMatch = ifNoneMatch;
            return controller;
        }

        private static string Text(IActionResult result) => Encoding.UTF8.GetString(Assert.IsType<FileContentResult>(result).FileContents);

        private static int Reads(Harness h, string path)
        {
            lock (h.Service.Calls)
                return h.Service.Calls.Count(c => c.Method == "GET" && c.PathAndQuery.Split('?')[0] == $"/api/{path}");
        }

        private static async Task Until(Func<bool> condition)
        {
            for (var n = 0; n < 200 && !condition(); n++)
                await Task.Delay(10);
            Assert.True(condition());
        }

        [Fact]
        public async Task Under_fresh_answers_from_memory()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Clock.Advance(TimeSpan.FromSeconds(29));
            var again = await Cached(h).Mail(h.Account);
            Assert.Contains("\"subject\":\"s\"", Text(again));
            Assert.Equal(1, Reads(h, "mail"));
        }

        [Fact]
        public async Task Under_stale_answers_old_and_refreshes_in_the_background()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Service.Answers["mail"] = """[{"id":"new","subject":"newer"}]""";
            h.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.Contains("\"subject\":\"s\"", Text(await Cached(h).Mail(h.Account)));
            await Until(() => Reads(h, "mail") == 2);
            await Until(() => Text(Cached(h).Mail(h.Account).Result).Contains("newer"));
        }

        [Fact]
        public async Task Past_stale_waits_for_Microsoft()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Service.Answers["mail"] = """[{"id":"new","subject":"newer"}]""";
            h.Clock.Advance(TimeSpan.FromSeconds(601));
            Assert.Contains("newer", Text(await Cached(h).Mail(h.Account)));
        }

        [Fact]
        public async Task Past_stale_falls_back_to_the_old_answer_when_Microsoft_fails()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Service.Failures["mail"] = (HttpStatusCode.BadGateway, null);
            h.Clock.Advance(TimeSpan.FromSeconds(601));
            Assert.Contains("\"subject\":\"s\"", Text(await Cached(h).Mail(h.Account)));
        }

        [Fact]
        public async Task A_message_that_is_gone_is_not_answered_from_old_data()
        {
            var h = Start();
            var id = h.Seal(IdKinds.MailMessage, MailOne);
            await Cached(h).Message(h.Account, id);
            h.Service.Answers.Remove($"mail/{MailOne}");
            h.Clock.Advance(TimeSpan.FromHours(1.5));
            Assert.IsType<NotFoundResult>(await Cached(h).Message(h.Account, id));
        }

        [Fact]
        public async Task An_old_answer_has_a_hard_end_even_as_a_fallback()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Service.Failures["mail"] = (HttpStatusCode.BadGateway, null);
            h.Clock.Advance(TimeSpan.FromSeconds(600 + 3600 + 1));
            await Assert.ThrowsAsync<HttpRequestException>(() => Cached(h).Mail(h.Account));
        }

        [Fact]
        public async Task Identical_requests_share_one_call()
        {
            var h = Start();
            h.Service.Hold = new TaskCompletionSource();
            var reads = Enumerable.Range(0, 5).Select(_ => Cached(h).Mail(h.Account)).ToList();
            await Until(() => Reads(h, "mail") == 1);
            h.Service.Hold.SetResult();
            await Task.WhenAll(reads);
            Assert.Equal(1, Reads(h, "mail"));
        }

        [Fact]
        public async Task A_slow_old_read_cannot_overwrite_a_newer_write()
        {
            var h = Start();
            h.Service.Hold = new TaskCompletionSource();
            var slow = Cached(h).Mail(h.Account);
            await Until(() => Reads(h, "mail") == 1);

            h.Service.Answers[$"mail/{MailOne}/read"] = "{}";
            var writer = h.Controller((u, l, r) => new MailController(u, l, r, h.Sealer, h.ResponseCache));
            var hold = h.Service.Hold;
            h.Service.Hold = null; // the write itself isn't held
            await writer.MarkRead(h.Account, h.Seal(IdKinds.MailMessage, MailOne), new MailReadRequest(true));
            h.Service.Answers["mail"] = """[{"id":"new","subject":"after write"}]""";

            hold.SetResult();
            await slow;
            Assert.Contains("after write", Text(await Cached(h).Mail(h.Account)));
        }

        [Fact]
        public async Task Entries_are_capped_in_count_and_the_oldest_goes_first()
        {
            var h = new Harness();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MicrosoftApi:BaseUrl"] = "http://ms.test", ["MicrosoftApi:Cache:MaxEntries"] = "2" }).Build();
            var cache = new MicrosoftResponseCache(h.Scopes(), config, h.Clock);
            foreach (var name in new[] { "a", "b", "c" })
                await cache.GetAsync(h.User, h.Account, name, CacheTimes.Lists, _ => Task.FromResult("[]"u8.ToArray()), default);
            Assert.Equal(2, cache.Count);
            var fetched = false;
            await cache.GetAsync(h.User, h.Account, "a", CacheTimes.Lists, _ => { fetched = true; return Task.FromResult("[]"u8.ToArray()); }, default);
            Assert.True(fetched);
        }

        [Fact]
        public async Task Times_can_be_configured()
        {
            var h = new Harness();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MicrosoftApi:Cache:Times:Lists:Fresh"] = "0" }).Build();
            var cache = new MicrosoftResponseCache(h.Scopes(), config, h.Clock);
            var calls = 0;
            Task<byte[]> Fetch(IServiceProvider _) { calls++; return Task.FromResult("[]"u8.ToArray()); }
            await cache.GetAsync(h.User, h.Account, "a", CacheTimes.Lists, Fetch, default);
            await cache.GetAsync(h.User, h.Account, "a", CacheTimes.Lists, Fetch, default);
            await Until(() => calls == 2);
        }

        [Fact]
        public async Task One_user_never_receives_anothers_entry()
        {
            var h = Start();
            var otherUser = Guid.NewGuid();
            var otherAccount = h.Accounts.Add(otherUser);
            var mine = Text(await Cached(h).Mail(h.Account));

            // The same raw data for another user: their own read, their own seal, and no reuse of my entry.
            var theirs = Text(await Cached(h, otherUser).Mail(otherAccount));
            Assert.Equal(2, Reads(h, "mail"));
            Assert.NotEqual(mine, theirs);
            var theirId = JsonDocument.Parse(theirs).RootElement[0].GetProperty("id").GetString();
            Assert.Null(h.Sealer.Unseal(h.User, h.Account, IdKinds.MailMessage, theirId));
            Assert.Equal(MailOne, h.Sealer.Unseal(otherUser, otherAccount, IdKinds.MailMessage, theirId));

            // Asking for my account as the other user finds nothing, though it is in memory.
            Assert.IsType<NotFoundResult>(await Cached(h, otherUser).Mail(h.Account));
        }

        [Fact]
        public async Task Two_accounts_of_a_user_keep_apart()
        {
            var h = Start();
            var first = Text(await Cached(h).Mail(h.Account));
            var second = Text(await Cached(h).Mail(h.OtherAccount));
            Assert.Equal(2, Reads(h, "mail"));
            Assert.NotEqual(first, second);
        }

        [Fact]
        public async Task Answers_are_sealed_like_the_live_route()
        {
            var h = Start();
            var cached = Text(await Cached(h).Mail(h.Account));
            var live = Assert.IsType<OkObjectResult>(await h.Controller((u, l, r) => new AccountsController(u, l, r, h.Sealer, h.ResponseCache)).Mail(h.Account));
            Assert.DoesNotContain(MailOne, cached);
            Assert.Equal(JsonSerializer.Serialize(live.Value), cached);
        }

        [Fact]
        public async Task Sends_etag_private_no_cache_and_age_and_answers_304_when_it_matches()
        {
            var h = Start();
            var first = Cached(h);
            await first.Mail(h.Account);
            var etag = first.Response.Headers.ETag.ToString();
            Assert.Matches("^\"[0-9a-f]{32}\"$", etag);
            Assert.Equal("private, no-cache", first.Response.Headers.CacheControl.ToString());

            h.Clock.Advance(TimeSpan.FromSeconds(7));
            var second = Cached(h, ifNoneMatch: etag);
            var result = await second.Mail(h.Account);
            Assert.Equal(StatusCodes.Status304NotModified, Assert.IsType<StatusCodeResult>(result).StatusCode);
            Assert.Equal("7", second.Response.Headers.Age.ToString());

            Assert.IsType<FileContentResult>(await Cached(h, ifNoneMatch: "\"other\"").Mail(h.Account));
        }

        [Fact]
        public async Task Throttling_keeps_Retry_After_and_makes_no_call_while_it_lasts()
        {
            var h = Start();
            h.Service.Failures["mail"] = (HttpStatusCode.TooManyRequests, 42);
            var controller = Cached(h);
            var result = await controller.Mail(h.Account);
            Assert.Equal(429, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal("42", controller.Response.Headers.RetryAfter.ToString());

            h.Clock.Advance(TimeSpan.FromSeconds(10));
            var again = Cached(h);
            Assert.Equal(429, Assert.IsType<ObjectResult>(await again.Mail(h.Account)).StatusCode);
            Assert.Equal("32", again.Response.Headers.RetryAfter.ToString());
            Assert.Equal(1, Reads(h, "mail"));

            h.Clock.Advance(TimeSpan.FromSeconds(33));
            h.Service.Failures.Remove("mail");
            Assert.IsType<FileContentResult>(await Cached(h).Mail(h.Account));
        }

        [Fact]
        public async Task A_live_route_keeps_Retry_After_too()
        {
            var h = Start();
            h.Service.Failures["mail"] = (HttpStatusCode.TooManyRequests, 42);
            var live = h.Controller((u, l, r) => new AccountsController(u, l, r, h.Sealer, h.ResponseCache));
            await live.Mail(h.Account);
            Assert.Equal("42", live.Response.Headers.RetryAfter.ToString());
        }

        [Fact]
        public async Task A_throttled_refresh_still_answers_what_is_held()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            h.Service.Failures["mail"] = (HttpStatusCode.TooManyRequests, 300);
            h.Clock.Advance(TimeSpan.FromSeconds(31));
            await Cached(h).Mail(h.Account);
            await Until(() => Reads(h, "mail") == 2);
            await Task.Delay(50);
            h.Clock.Advance(TimeSpan.FromSeconds(31));
            Assert.IsType<FileContentResult>(await Cached(h).Mail(h.Account));
            Assert.Equal(2, Reads(h, "mail"));
        }

        [Fact]
        public async Task Upstream_calls_are_limited_per_user()
        {
            var h = Start();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MicrosoftApi:Cache:CallsPerMinute"] = "1" }).Build();
            var cache = new MicrosoftResponseCache(h.Scopes(), config, h.Clock);
            Task<byte[]> Fetch(IServiceProvider _) => Task.FromResult("[]"u8.ToArray());
            await cache.GetAsync(h.User, h.Account, "a", CacheTimes.Lists, Fetch, default);
            var limited = await Assert.ThrowsAsync<MicrosoftThrottledException>(() => cache.GetAsync(h.User, h.Account, "b", CacheTimes.Lists, Fetch, default));
            Assert.True(limited.RetryAfter > TimeSpan.Zero);

            // Someone else isn't held up, and a user's own memory still answers.
            await cache.GetAsync(Guid.NewGuid(), h.Account, "b", CacheTimes.Lists, Fetch, default);
            await cache.GetAsync(h.User, h.Account, "a", CacheTimes.Lists, Fetch, default);
        }

        [Fact]
        public async Task Bootstrap_answers_the_first_screen_and_shares_its_parts_with_the_list_routes()
        {
            var h = Start();
            var body = JsonDocument.Parse(Text(await Cached(h).Bootstrap(h.Account))).RootElement;
            Assert.Equal("s", body.GetProperty("mail")[0].GetProperty("subject").GetString());
            Assert.Equal("Inbox", body.GetProperty("folders")[0].GetProperty("displayName").GetString());
            Assert.Equal("group", body.GetProperty("chats")[0].GetProperty("chatType").GetString());
            Assert.DoesNotContain(MailOne, body.GetRawText());

            await Cached(h).Mail(h.Account);
            await Cached(h).Chats(h.Account);
            await Cached(h).Folders(h.Account);
            Assert.Equal(1, Reads(h, "mail"));
            Assert.Equal(1, Reads(h, "chats"));
            Assert.Equal(1, Reads(h, "mail/folders"));
        }

        [Fact]
        public async Task Mail_writes_go_straight_to_Microsoft_and_forget_what_they_changed()
        {
            var h = Start();
            var id = h.Seal(IdKinds.MailMessage, MailOne);
            h.Service.Answers[$"mail/{MailOne}/read"] = "{}";
            await Cached(h).Bootstrap(h.Account);
            await Cached(h).Message(h.Account, id);

            await h.Controller((u, l, r) => new MailController(u, l, r, h.Sealer, h.ResponseCache)).MarkRead(h.Account, id, new MailReadRequest(true));
            Assert.Contains(h.Service.Calls, c => c.Method == "POST" && c.PathAndQuery == $"/api/mail/{MailOne}/read");

            h.Service.Answers["mail"] = """[{"id":"new","subject":"after write"}]""";
            Assert.Contains("after write", Text(await Cached(h).Mail(h.Account)));
            Assert.Contains("after write", Text(await Cached(h).Bootstrap(h.Account)));
            await Cached(h).Message(h.Account, id);
            Assert.Equal(2, Reads(h, $"mail/{MailOne}"));
            Assert.Equal(1, Reads(h, "chats")); // chats didn't change
        }

        [Fact]
        public async Task Marking_a_chat_read_forgets_the_chat_list()
        {
            var h = Start();
            h.Service.Answers["chats/19:c@thread.v2/read"] = "{}";
            await Cached(h).Chats(h.Account);
            await h.Controller((u, l, r) => new AccountsController(u, l, r, h.Sealer, h.ResponseCache)).MarkChatRead(h.Account, h.Seal(IdKinds.Chat, "19:c@thread.v2"));
            await Cached(h).Chats(h.Account);
            Assert.Equal(2, Reads(h, "chats"));
        }

        [Fact]
        public async Task A_failed_write_keeps_the_cache()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            var writer = h.Controller((u, l, r) => new MailController(u, l, r, h.Sealer, h.ResponseCache));
            Assert.IsType<NotFoundResult>(await writer.MarkRead(h.Account, h.Seal(IdKinds.MailMessage, MailOne), new MailReadRequest(true)));
            await Cached(h).Mail(h.Account);
            Assert.Equal(1, Reads(h, "mail"));
        }

        [Fact]
        public async Task Signing_out_clears_only_that_users_entries()
        {
            var h = Start();
            var otherUser = Guid.NewGuid();
            var otherAccount = h.Accounts.Add(otherUser);
            await Cached(h).Mail(h.Account);
            await Cached(h, otherUser).Mail(otherAccount);

            Assert.IsType<NoContentResult>(await Cached(h).Clear());
            Assert.Equal(1, h.ResponseCache.Count);
            await Cached(h, otherUser).Mail(otherAccount);
            Assert.Equal(2, Reads(h, "mail"));
        }

        [Fact]
        public async Task The_purge_event_clears_that_users_entries()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            await new MicrosoftUserPurgeHandler(new PendingLinkStore(h.Vault), h.Scheduled(), h.ResponseCache).HandleAsync(new PluginUserPurgeEvent(h.User));
            Assert.Equal(0, h.ResponseCache.Count);
        }

        [Fact]
        public async Task Unlinking_clears_that_accounts_entries()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            await Cached(h).Mail(h.OtherAccount);
            var provider = new MicrosoftLinkProvider(new MicrosoftServiceClient(new FakeFactory(h.Service), h.Config), new MicrosoftAccountLinker(h.Accounts), h.Accounts, new PasswordAttemptLimiter(TimeProvider.System), h.ResponseCache, NullLogger<MicrosoftLinkProvider>.Instance);
            Assert.True(await provider.UnlinkAsync(h.User, h.Account.ToString()));
            Assert.Equal(1, h.ResponseCache.Count);
            Assert.IsType<NotFoundResult>(await Cached(h).Mail(h.Account));
        }

        [Fact]
        public async Task A_read_running_during_a_clear_cannot_put_data_back()
        {
            var h = Start();
            h.Service.Hold = new TaskCompletionSource();
            var slow = Cached(h).Mail(h.Account);
            await Until(() => Reads(h, "mail") == 1);
            h.ResponseCache.ClearUser(h.User);
            h.Service.Hold.SetResult();
            await slow;
            Assert.Equal(0, h.ResponseCache.Count);
        }

        [Fact]
        public async Task The_sweep_drops_what_ran_out_and_accounts_unlinked_through_core()
        {
            var h = Start();
            var provider = new ServiceCollection().AddSingleton(h.ResponseCache).AddSingleton<ILinkedAccounts>(h.Accounts).BuildServiceProvider();

            await Cached(h).Mail(h.Account);
            await Cached(h).Mail(h.OtherAccount);
            await h.Accounts.RemoveAsync(h.User, h.OtherAccount);
            await new MicrosoftResponseCacheSweepTask().ExecuteAsync(provider, default);
            Assert.Equal(1, h.ResponseCache.Count);

            h.Clock.Advance(TimeSpan.FromSeconds(600 + 3600 + 1));
            await new MicrosoftResponseCacheSweepTask().ExecuteAsync(provider, default);
            Assert.Equal(0, h.ResponseCache.Count);
        }

        [Fact]
        public async Task Live_routes_never_read_or_fill_the_cache()
        {
            var h = Start();
            var live = () => h.Controller((u, l, r) => new AccountsController(u, l, r, h.Sealer, h.ResponseCache));
            await Cached(h).Mail(h.Account);
            await live().Mail(h.Account);
            await live().Mail(h.Account);
            Assert.Equal(3, Reads(h, "mail"));
            Assert.Equal(1, h.ResponseCache.Count);
        }

        [Fact]
        public async Task Mcp_callers_cannot_use_the_cached_routes()
        {
            var h = Start();
            await Cached(h).Mail(h.Account);
            var mcp = Cached(h);
            mcp.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", h.User.ToString())], "SchulyMcpToken"));
            Assert.IsType<NotFoundResult>(await mcp.Mail(h.Account));
            Assert.Equal(1, Reads(h, "mail"));
        }
    }
}
