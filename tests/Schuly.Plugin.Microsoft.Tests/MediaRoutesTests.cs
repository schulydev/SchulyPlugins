using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    /// <summary>Range through the file routes, the real wait of a throttled call, and the tickets a &lt;video&gt; plays with.</summary>
    public class MediaRoutesTests
    {
        private const string Chat = "19:rawmediachat@thread.v2";
        private const string Msg = "1730000000500";
        private const string Att = "rawmediaattachment";

        private sealed class RangeService : HttpMessageHandler
        {
            public readonly List<(string Path, string? Range)> Calls = [];
            public HttpStatusCode Status = HttpStatusCode.PartialContent;
            public int? RetryAfter;
            public string Type = "video/mp4";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var range = request.Headers.TryGetValues("Range", out var values) ? values.Single() : null;
                lock (Calls)
                    Calls.Add((request.RequestUri!.AbsolutePath, range));
                var response = new HttpResponseMessage(Status) { Content = new ByteArrayContent(Status == HttpStatusCode.PartialContent ? Encoding.ASCII.GetBytes("abcde") : Status == HttpStatusCode.OK ? Encoding.ASCII.GetBytes("abcdefghij") : []) };
                response.Content.Headers.ContentType = new(Type);
                response.Content.Headers.TryAddWithoutValidation("Content-Disposition", "attachment; filename*=UTF-8''clip.mp4");
                if (Status == HttpStatusCode.PartialContent)
                    response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes 0-4/10");
                if (Status == HttpStatusCode.RequestedRangeNotSatisfiable)
                    response.Content.Headers.TryAddWithoutValidation("Content-Range", "bytes */10");
                if (RetryAfter is { } seconds)
                    response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
                return Task.FromResult(response);
            }
        }

        private sealed class Rig
        {
            public readonly Harness H = new();
            public readonly RangeService Service = new();
            public readonly MediaTickets Tickets = new();

            public MicrosoftAccountReader Reader()
            {
                return new MicrosoftAccountReader(H.Accounts, new MicrosoftServiceClient(new FakeFactory(Service), H.Config));
            }

            public string ChatId => H.Seal(IdKinds.Chat, Chat);
            public string MsgId => H.Seal(IdKinds.ChatMessage, Msg);
            public string AttId => H.Seal(IdKinds.ChatFile, Att);

            public ChatsController Chats(string? range = null) => Ready(new ChatsController(new FakeUser(H.User), H.Accounts, Reader(), H.Scheduled(), H.Sealer, H.ResponseCache), range);

            public MediaStreamController Media(string? range = null, string? cookie = null, Guid? asUser = null)
            {
                var controller = Ready(new MediaStreamController(new FakeUser(asUser ?? H.User), H.Accounts, Reader(), H.Sealer, Tickets), range);
                if (cookie is not null)
                    controller.Request.Headers.Cookie = $"{MediaTickets.CookieName}={cookie}";
                return controller;
            }

            private static T Ready<T>(T controller, string? range) where T : ControllerBase
            {
                var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().AddMvcCore().Services.BuildServiceProvider() };
                context.Response.Body = new MemoryStream();
                if (range is not null)
                    context.Request.Headers.Range = range;
                controller.ControllerContext = new ControllerContext { HttpContext = context };
                return controller;
            }
        }

        private static async Task<string> Execute(ControllerBase controller, IActionResult result)
        {
            await result.ExecuteResultAsync(new ActionContext(controller.HttpContext, new RouteData(), new ActionDescriptor()));
            var body = (MemoryStream)controller.Response.Body;
            return Encoding.ASCII.GetString(body.ToArray());
        }

        [Fact]
        public async Task A_range_comes_back_as_206_with_its_headers_and_type()
        {
            var rig = new Rig();
            var controller = rig.Chats("bytes=0-4");
            var result = await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            var body = await Execute(controller, result);
            Assert.Equal(206, controller.Response.StatusCode);
            Assert.Equal("abcde", body);
            Assert.Equal("bytes 0-4/10", controller.Response.Headers.ContentRange);
            Assert.Equal("bytes", controller.Response.Headers.AcceptRanges);
            Assert.Equal("video/mp4", controller.Response.ContentType);
            Assert.Equal("bytes=0-4", rig.Service.Calls.Single().Range);
        }

        [Fact]
        public async Task No_range_is_the_whole_file_and_says_it_can_seek()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.OK;
            var controller = rig.Chats();
            var body = await Execute(controller, await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId));
            Assert.Equal(200, controller.Response.StatusCode);
            Assert.Equal("abcdefghij", body);
            Assert.Equal("bytes", controller.Response.Headers.AcceptRanges);
            Assert.Null(rig.Service.Calls.Single().Range);
        }

        [Fact]
        public async Task File_answers_stay_attachments_that_cannot_run()
        {
            var rig = new Rig();
            var controller = rig.Chats("bytes=0-4");
            await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.StartsWith("attachment", controller.Response.Headers.ContentDisposition.ToString());
            Assert.Equal("sandbox; default-src 'none'", controller.Response.Headers.ContentSecurityPolicy);
            Assert.Equal("nosniff", controller.Response.Headers.XContentTypeOptions);
            Assert.Equal("no-store", controller.Response.Headers.CacheControl);
        }

        [Fact]
        public async Task A_range_past_the_end_stays_416()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.RequestedRangeNotSatisfiable;
            var controller = rig.Chats("bytes=99-");
            var result = await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.Equal(416, Assert.IsType<StatusCodeResult>(result).StatusCode);
            Assert.Equal("bytes */10", controller.Response.Headers.ContentRange);
        }

        [Theory]
        [InlineData("bytes=-5")]
        [InlineData("bytes=0-1,4-5")]
        [InlineData("items=0-1")]
        [InlineData("bytes=0-1\r\nX-Evil: 1")]
        public async Task Any_other_range_is_never_passed_on(string range)
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.OK;
            var controller = rig.Chats(range);
            await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.Null(rig.Service.Calls.Single().Range);
        }

        // Production showed exactly 60 on two requests 19 s apart: the file routes dropped the service's Retry-After and the controller's fallback answered.
        [Fact]
        public async Task A_throttled_file_keeps_the_real_wait()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.TooManyRequests;
            rig.Service.RetryAfter = 62;
            var controller = rig.Chats();
            var result = await controller.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.Equal(429, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal("62", controller.Response.Headers.RetryAfter);
        }

        [Fact]
        public async Task A_throttled_thumbnail_keeps_the_real_wait()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.TooManyRequests;
            rig.Service.RetryAfter = 41;
            var controller = rig.Chats();
            var result = await controller.ChatFileThumbnail(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.Equal(429, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal("41", controller.Response.Headers.RetryAfter);
        }

        [Fact]
        public async Task While_microsoft_asks_to_wait_files_make_no_call_and_say_what_is_left()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.TooManyRequests;
            rig.Service.RetryAfter = 62;
            await rig.Chats().ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            Assert.Single(rig.Service.Calls);

            var second = rig.Chats();
            Assert.Equal(429, Assert.IsType<ObjectResult>(await second.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId)).StatusCode);
            var third = rig.Chats();
            Assert.Equal(429, Assert.IsType<ObjectResult>(await third.ChatFileThumbnail(rig.H.Account, rig.ChatId, rig.MsgId, rig.H.Seal(IdKinds.ChatFile, "otherattachment"))).StatusCode);
            Assert.Single(rig.Service.Calls);
            Assert.InRange(int.Parse(second.Response.Headers.RetryAfter!), 1, 62);
        }

        [Fact]
        public async Task A_throttled_file_does_not_stop_photos_or_the_other_account()
        {
            var rig = new Rig();
            rig.Service.Status = HttpStatusCode.TooManyRequests;
            rig.Service.RetryAfter = 62;
            await rig.Chats().ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);

            rig.Service.Status = HttpStatusCode.OK;
            rig.Service.RetryAfter = null;
            await rig.Reader().GetPhotoAsync(rig.H.User, rig.H.Account, "someone", 64, default);
            await rig.Reader().OpenStreamAsync(rig.H.User, rig.H.OtherAccount, "drives/d/items/i/content", default);
            Assert.Equal(3, rig.Service.Calls.Count);
        }

        [Fact]
        public async Task A_missing_photo_costs_one_call_and_the_browser_may_remember_it()
        {
            var h = new Harness();
            var first = await h.Reader().GetPhotoAsync(h.User, h.Account, "nophoto", 64, default);
            var second = await h.Reader().GetPhotoAsync(h.User, h.Account, "nophoto", 64, default);
            Assert.Null(first.Bytes);
            Assert.Null(second.Bytes);
            Assert.Single(h.Service.Calls);
            Assert.Equal(3600, MicrosoftAccountReader.MissMaxAgeSeconds);
        }

        // ---- tickets ----

        private static (string Handle, string Secret) Opened(ControllerBase controller, IActionResult result)
        {
            var url = (string)Assert.IsType<OkObjectResult>(result).Value!.GetType().GetProperty("url")!.GetValue(((OkObjectResult)result).Value)!;
            var cookie = controller.Response.Headers.SetCookie.ToString();
            var secret = cookie[(MediaTickets.CookieName.Length + 1)..cookie.IndexOf(';')];
            return (url[(url.LastIndexOf('/') + 1)..], secret);
        }

        [Fact]
        public async Task A_ticket_secret_only_travels_in_a_locked_down_cookie()
        {
            var rig = new Rig();
            var media = rig.Media();
            var result = await media.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId);
            var (handle, secret) = Opened(media, result);
            var cookie = media.Response.Headers.SetCookie.ToString().ToLowerInvariant();
            Assert.Contains("httponly", cookie);
            Assert.Contains("secure", cookie);
            Assert.Contains("samesite=strict", cookie);
            Assert.Contains($"path=/api/plugins/microsoft/media/{handle}".ToLowerInvariant(), cookie);
            Assert.DoesNotContain(secret, System.Text.Json.JsonSerializer.Serialize(((OkObjectResult)result).Value));
            Assert.Empty(rig.Service.Calls);
        }

        [Fact]
        public async Task A_ticket_plays_with_range_without_the_session()
        {
            var rig = new Rig();
            var opener = rig.Media();
            var (handle, secret) = Opened(opener, await opener.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId));

            var player = rig.Media("bytes=0-4", secret);
            var body = await Execute(player, await player.Play(handle));
            Assert.Equal(206, player.Response.StatusCode);
            Assert.Equal("abcde", body);
            Assert.Equal($"/api/chats/{Chat}/messages/{Msg}/files/{Att}", Uri.UnescapeDataString(rig.Service.Calls.Single().Path));
        }

        [Fact]
        public async Task A_ticket_needs_both_parts()
        {
            var rig = new Rig();
            var opener = rig.Media();
            var (handle, secret) = Opened(opener, await opener.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId));

            Assert.IsType<NotFoundResult>(await rig.Media().Play(handle));
            Assert.IsType<NotFoundResult>(await rig.Media(cookie: "wrong").Play(handle));
            Assert.IsType<NotFoundResult>(await rig.Media(cookie: secret).Play("wrong"));
            Assert.Empty(rig.Service.Calls);
        }

        [Fact]
        public async Task A_ticket_plays_only_video_and_audio()
        {
            var rig = new Rig();
            rig.Service.Type = "text/html";
            var opener = rig.Media();
            var (handle, secret) = Opened(opener, await opener.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId));
            var player = rig.Media(cookie: secret);
            Assert.Equal(415, Assert.IsType<ObjectResult>(await player.Play(handle)).StatusCode);
        }

        [Fact]
        public async Task A_ticket_stops_with_the_linked_account()
        {
            var rig = new Rig();
            var opener = rig.Media();
            var (handle, secret) = Opened(opener, await opener.ChatFile(rig.H.Account, rig.ChatId, rig.MsgId, rig.AttId));
            await rig.H.Accounts.RemoveAsync(rig.H.User, rig.H.Account);
            Assert.IsType<NotFoundResult>(await rig.Media(cookie: secret).Play(handle));
            Assert.Empty(rig.Service.Calls);
        }

        [Fact]
        public async Task A_foreign_sealed_id_opens_no_ticket()
        {
            var rig = new Rig();
            var other = new Harness();
            var foreign = other.Seal(IdKinds.Chat, Chat);
            var media = rig.Media();
            Assert.IsType<NotFoundResult>(await media.ChatFile(rig.H.Account, foreign, rig.MsgId, rig.AttId));
            Assert.False(media.Response.Headers.ContainsKey("Set-Cookie"));
        }
    }
}
