using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    /// <summary>The live chat stream: ids sealed, nothing unknown passed on, cached answers dropped before the browser hears of a change, and the upstream closed with the caller.</summary>
    public class LiveChatTests
    {
        private const string Chat = "19:rawlivechat@thread.v2";

        /// <summary>Stays open after its lines until the read is cancelled, like a quiet feed.</summary>
        private sealed class OpenStream(byte[] lines) : Stream
        {
            private int at;
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                if (at < lines.Length)
                {
                    var n = Math.Min(buffer.Length, lines.Length - at);
                    lines.AsMemory(at, n).CopyTo(buffer);
                    at += n;
                    return n;
                }
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return 0;
            }

            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        private sealed class LiveService(Func<HttpResponseMessage> answer) : HttpMessageHandler
        {
            public string? Path;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Path = request.RequestUri!.AbsolutePath;
                return Task.FromResult(answer());
            }
        }

        private sealed class Rig(Func<HttpResponseMessage> answer)
        {
            public readonly Harness H = new();
            public readonly LiveService Service = new(answer);

            public ChatsController Chats()
            {
                var reader = new MicrosoftAccountReader(H.Accounts, new MicrosoftServiceClient(new FakeFactory(Service), H.Config));
                var controller = new ChatsController(new FakeUser(H.User), H.Accounts, reader, H.Scheduled(), H.Sealer, H.ResponseCache);
                var context = new DefaultHttpContext();
                context.Response.Body = new MemoryStream();
                controller.ControllerContext = new ControllerContext { HttpContext = context };
                return controller;
            }
        }

        private static HttpResponseMessage Feed(params string[] lines) => new(HttpStatusCode.OK) { Content = new StringContent(string.Concat(lines.Select(l => l + "\n")), Encoding.UTF8, "application/x-ndjson") };

        private static string Body(ControllerBase controller) => Encoding.UTF8.GetString(((MemoryStream)controller.Response.Body).ToArray());

        [Fact]
        public void An_event_carries_a_sealed_chat_id_and_nothing_else()
        {
            var h = new Harness();
            var ids = h.Scope();
            var relayed = LiveChatEvents.Relay($$"""{"kind":"message","chatId":"{{Chat}}","fromMe":true,"sender":"Bo","body":"secret"}""", ids);
            Assert.True(relayed.Changed);
            Assert.StartsWith("data: ", relayed.Sse);
            Assert.DoesNotContain("rawlivechat", relayed.Sse);
            Assert.DoesNotContain("secret", relayed.Sse);
            Assert.DoesNotContain("Bo", relayed.Sse!.Replace(h.Seal(IdKinds.Chat, Chat), ""));
            Assert.Contains($"\"chatId\":\"{h.Seal(IdKinds.Chat, Chat)}\"", relayed.Sse);
            Assert.Contains("\"fromMe\":true", relayed.Sse);
        }

        [Theory]
        [InlineData("not json")]
        [InlineData("[]")]
        [InlineData("""{"kind":"typing","chatId":"19:a@thread.v2"}""")]
        [InlineData("""{"kind":"message"}""")]
        [InlineData("""{"kind":"message","chatId":""}""")]
        [InlineData("""{"kind":"message","chatId":7}""")]
        [InlineData("""{"kind":7}""")]
        public void Anything_unknown_is_dropped(string line)
        {
            var relayed = LiveChatEvents.Relay(line, new Harness().Scope());
            Assert.Null(relayed.Sse);
            Assert.False(relayed.Changed);
        }

        [Fact]
        public void A_ping_is_a_comment_and_changes_nothing()
        {
            var relayed = LiveChatEvents.Relay("""{"kind":"ping"}""", new Harness().Scope());
            Assert.Equal(": ping\n\n", relayed.Sse);
            Assert.False(relayed.Changed);
        }

        [Fact]
        public async Task The_route_streams_sealed_events_and_drops_cached_chats_first()
        {
            var rig = new Rig(() => Feed("""{"kind":"ping"}""", """{"kind":"connected"}""", $$"""{"kind":"message_deleted","chatId":"{{Chat}}","fromMe":false}"""));
            var fetches = 0;
            Task<byte[]> Fetch(IServiceProvider _)
            {
                fetches++;
                return Task.FromResult("[]"u8.ToArray());
            }
            var cache = rig.H.ResponseCache;
            await cache.GetAsync(rig.H.User, rig.H.Account, $"{MicrosoftResponseCache.ChatsPrefix}/x/messages?top=30", CacheTimes.ChatMessages, Fetch, default);
            var controller = rig.Chats();
            var result = await controller.ChatsLive(rig.H.Account);
            Assert.IsType<EmptyResult>(result);
            Assert.EndsWith("/api/chats/live", rig.Service.Path);
            Assert.Equal("text/event-stream", controller.Response.ContentType);
            Assert.Equal("no-store", controller.Response.Headers.CacheControl);
            var body = Body(controller);
            Assert.Contains(": ping\n\n", body);
            Assert.Contains("data: {\"kind\":\"connected\"}\n\n", body);
            Assert.Contains($"\"chatId\":\"{rig.H.Seal(IdKinds.Chat, Chat)}\"", body);
            Assert.DoesNotContain("rawlivechat", body);
            await cache.GetAsync(rig.H.User, rig.H.Account, $"{MicrosoftResponseCache.ChatsPrefix}/x/messages?top=30", CacheTimes.ChatMessages, Fetch, default);
            Assert.Equal(2, fetches);
        }

        [Fact]
        public async Task The_caller_leaving_ends_it_quietly()
        {
            var open = new StreamContent(new OpenStream(Encoding.UTF8.GetBytes("{\"kind\":\"connected\"}\n")));
            var rig = new Rig(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = open });
            var controller = rig.Chats();
            using var leaving = new CancellationTokenSource();
            var running = controller.ChatsLive(rig.H.Account, leaving.Token);
            for (var i = 0; i < 200 && !Body(controller).Contains("connected"); i++)
                await Task.Delay(10);
            Assert.Contains("connected", Body(controller));
            Assert.False(running.IsCompleted);
            leaving.Cancel();
            Assert.IsType<EmptyResult>(await running.WaitAsync(TimeSpan.FromSeconds(5)));
        }

        [Theory]
        [InlineData(HttpStatusCode.ServiceUnavailable, 503)]
        [InlineData(HttpStatusCode.Unauthorized, 409)]
        [InlineData(HttpStatusCode.NotFound, 404)]
        [InlineData(HttpStatusCode.TooManyRequests, 429)]
        public async Task A_refusal_before_the_stream_is_a_real_status(HttpStatusCode upstream, int expected)
        {
            var rig = new Rig(() => new HttpResponseMessage(upstream) { Content = new StringContent("{}") });
            var result = await rig.Chats().ChatsLive(rig.H.Account);
            Assert.Equal(expected, ((IStatusCodeActionResult)result).StatusCode);
        }

        [Fact]
        public async Task Someone_elses_account_does_not_exist()
        {
            var rig = new Rig(() => Feed());
            var controller = rig.Chats();
            Assert.IsType<NotFoundResult>(await controller.ChatsLive(Guid.NewGuid()));
            Assert.Null(rig.Service.Path);
        }
    }
}
