using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Kiota.Abstractions;
using Schuly.Plugin.Schulware.Controllers;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class StudentIdCardHtmlTests
    {
        // A made-up QR payload; it must come out byte for byte.
        private const string Qr = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

        [Fact]
        public void The_card_layout_and_data_images_stay_unchanged()
        {
            var html = $"<div style=\"position:absolute;left:10px;top:20px;background:url('{Qr}')\"><img src=\"{Qr}\" style=\"width:80px\" alt=\"QR\"><span class=\"name\">Muster Max</span></div>";

            var clean = StudentIdCardHtml.Sanitize(html);

            Assert.Contains($"src=\"{Qr}\"", clean);
            Assert.Contains($"url('{Qr}')", clean);
            Assert.Contains("position:absolute;left:10px;top:20px", clean);
            Assert.Contains("<span class=\"name\">Muster Max</span>", clean);
        }

        [Theory]
        [InlineData("<script>alert(1)</script>", "alert")]
        [InlineData("<SCRIPT src=\"https://evil.example/x.js\"></SCRIPT>", "evil")]
        [InlineData("<iframe src=\"https://evil.example\"><p>x</p></iframe>", "evil")]
        [InlineData("<object data=\"https://evil.example/x\"><embed src=\"x\"></object>", "evil")]
        [InlineData("<img src=x onerror=\"alert(1)\">", "alert")]
        [InlineData("<div ONCLICK='steal()'>a</div>", "steal")]
        [InlineData("<a href=\"javascript:alert(1)\">a</a>", "javascript")]
        [InlineData("<a href=\"https://evil.example\">a</a>", "evil")]
        [InlineData("<img src=\"https://tracker.example/p.gif\">", "tracker")]
        [InlineData("<img src=\"  jav&#x09;ascript:alert(1)\">", "ascript")]
        [InlineData("<link rel=stylesheet href=\"https://evil.example/a.css\">", "evil")]
        [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\">", "evil")]
        [InlineData("<base href=\"https://evil.example/\">", "evil")]
        [InlineData("<form action=\"https://evil.example\"><input formaction=\"https://evil.example\"></form>", "evil")]
        [InlineData("<div style=\"background:url(https://evil.example/a.png)\">a</div>", "evil")]
        [InlineData("<div style=\"background:\\75 rl(https://evil.example/a.png)\">a</div>", "evil")]
        [InlineData("<style>@import url(https://evil.example/a.css); .a{color:red}</style>", "evil")]
        [InlineData("<style>.a{background-image:url(\"//evil.example/a.png\")}</style>", "evil")]
        [InlineData("<div style=\"width:expression(alert(1))\">a</div>", "expression")]
        [InlineData("<svg><image xlink:href=\"https://evil.example/a.png\"/></svg>", "evil")]
        [InlineData("<svg><script>alert(1)</script></svg>", "alert")]
        [InlineData("<!-- <script>alert(1)</script> -->", "alert")]
        [InlineData("<div title=\"x\" data-u=\"https://evil.example\">a</div>", "evil")]
        [InlineData("<style></style><script>alert(1)</script>", "alert")]
        [InlineData("<textarea></textarea><img src=x onerror=alert(1)>", "alert")]
        public void Code_navigation_and_remote_loads_are_removed(string html, string forbidden)
        {
            var clean = StudentIdCardHtml.Sanitize(html);

            Assert.DoesNotContain(forbidden, clean, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void An_end_tag_inside_style_cannot_break_out()
        {
            var clean = StudentIdCardHtml.Sanitize("<style>.a{content:\"\\3c/style\\3e<script>alert(1)</script>\"}</style>");

            Assert.DoesNotContain("<script", clean, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Quotes_in_a_single_quoted_attribute_cannot_open_a_new_attribute()
        {
            var clean = StudentIdCardHtml.Sanitize("<div title='a\" onmouseover=\"alert(1)'>x</div>");

            Assert.Equal("<div title=\"a&quot; onmouseover=&quot;alert(1)\">x</div>", clean);
        }
    }

    public class StudentIdCardServiceTests
    {
        private sealed class FakeSource : IStudentIdCardSource
        {
            public Func<string?> Mobile = () => "<p>mobile</p>";
            public Func<string?> Web = () => "<p>web</p>";
            public int MobileCalls;
            public int WebCalls;
            public int? ReportId;

            public Task<string?> GetMobileAsync(int reportId, CancellationToken ct)
            {
                MobileCalls++;
                ReportId = reportId;
                return Task.FromResult(Mobile());
            }

            public Task<string?> GetWebAsync(CancellationToken ct)
            {
                WebCalls++;
                return Task.FromResult(Web());
            }
        }

        private static StudentIdCardService Service(string? reportId = null) =>
            new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["StudentIdCard:ReportId"] = reportId }).Build());

        private static Func<string?> Refused(int status) => () => throw new ApiException { ResponseStatusCode = status };

        [Fact]
        public async Task The_mobile_report_is_used_first_with_the_default_report()
        {
            var source = new FakeSource();

            var card = await Service().FetchAsync(source, true, default);

            Assert.Equal("mobile", card.Source);
            Assert.Equal("<p>mobile</p>", card.Html);
            Assert.Equal(50505, source.ReportId);
            Assert.Equal(0, source.WebCalls);
        }

        [Fact]
        public async Task The_report_id_comes_from_config()
        {
            var source = new FakeSource();
            await Service("50600").FetchAsync(source, true, default);
            Assert.Equal(50600, source.ReportId);
        }

        [Theory]
        [InlineData(403)]
        [InlineData(404)]
        public async Task A_school_without_the_mobile_report_falls_back_to_the_web_page(int status)
        {
            var source = new FakeSource { Mobile = Refused(status) };

            var card = await Service().FetchAsync(source, true, default);

            Assert.Equal("web", card.Source);
            Assert.Equal(1, source.WebCalls);
        }

        [Fact]
        public async Task Without_the_mobile_api_only_the_web_page_is_asked()
        {
            var source = new FakeSource();

            var card = await Service().FetchAsync(source, false, default);

            Assert.Equal("web", card.Source);
            Assert.Equal(0, source.MobileCalls);
        }

        [Fact]
        public async Task An_empty_mobile_answer_also_falls_back()
        {
            var card = await Service().FetchAsync(new FakeSource { Mobile = () => "  " }, true, default);
            Assert.Equal("web", card.Source);
        }

        [Fact]
        public async Task No_card_anywhere_is_not_offered()
        {
            var ex = await Assert.ThrowsAsync<StudentIdCardException>(() => Service().FetchAsync(new FakeSource { Mobile = Refused(404), Web = () => null }, true, default));
            Assert.Equal(404, ex.StatusCode);
            Assert.Equal("not_offered", ex.Code);
        }

        [Fact]
        public async Task A_refused_token_is_an_expired_sign_in()
        {
            var source = new FakeSource { Mobile = Refused(401) };
            var ex = await Assert.ThrowsAsync<StudentIdCardException>(() => Service().FetchAsync(source, true, default));
            Assert.Equal("signin_expired", ex.Code);
            Assert.Equal(0, source.WebCalls);
        }

        [Fact]
        public async Task A_server_error_is_unavailable()
        {
            var ex = await Assert.ThrowsAsync<StudentIdCardException>(() => Service().FetchAsync(new FakeSource { Mobile = Refused(500) }, true, default));
            Assert.Equal(502, ex.StatusCode);
        }

        [Fact]
        public async Task The_card_is_sanitized()
        {
            var card = await Service().FetchAsync(new FakeSource { Mobile = () => "<p onclick=\"x()\">a</p><script>x()</script>" }, true, default);
            Assert.Equal("<p>a</p>", card.Html);
        }

        [Fact]
        public async Task Private_mode_has_no_web_fallback()
        {
            var source = new StatelessStudentIdCardSource(null!);
            Assert.Null(await source.GetWebAsync(default));
        }
    }

    public class StudentIdCardControllerTests
    {
        private static readonly Guid Owner = Guid.NewGuid();
        private static readonly Guid Stranger = Guid.NewGuid();
        private const string Card = "<div style=\"position:absolute\"><img src=\"data:image/png;base64,AAAA\"></div>";

        /// <summary>Plays SchulwareAPI: the mobile report answers <see cref="MobileStatus"/>, the web page always has the card.</summary>
        private sealed class Handler : HttpMessageHandler
        {
            public HttpStatusCode MobileStatus = HttpStatusCode.OK;
            public readonly List<string> Paths = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                Paths.Add(path);
                if (path.StartsWith("/api/mobile/studentidcard/"))
                    return Task.FromResult(MobileStatus == HttpStatusCode.OK ? Json(JsonSerializer.Serialize(new { html = Card })) : new HttpResponseMessage(MobileStatus) { Content = new StringContent("{}", Encoding.UTF8, "application/json") });
                if (path == "/api/websession/scrape")
                    return Task.FromResult(Json(JsonSerializer.Serialize(new { success = true, student_id = new { html = Card } })));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private sealed class Factory(Handler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, false);
        }

        private sealed class Env
        {
            public readonly Handler Handler = new();
            public readonly FakeVault Vault = new();
            public readonly SchulwareDbContext Db = new(new DbContextOptionsBuilder<SchulwareDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
            public readonly CapturingLogger<StudentIdCardController> Logger = new();
            public readonly Guid AccountId = Guid.NewGuid();

            public Env(bool withSecrets = true)
            {
                var account = new SchulwareAccount { Id = AccountId, ApplicationUserId = Owner, SchulnetzBaseUrl = "https://schulnetz.example.ch", SchulwareApiBaseUrl = "http://schulware.test", MobileTokenExpiresAt = DateTime.UtcNow.AddHours(1) };
                Db.Accounts.Add(account);
                Db.SaveChanges();
                if (!withSecrets) return;
                account.MobileAccessToken = "fake-token";
                account.WebSessionId = "fake-session";
                new AccountSecretStore(Vault).Save(account);
            }

            public StudentIdCardController Controller(Guid user)
            {
                var factory = new Factory(Handler);
                var refresh = new TokenRefreshService(factory, Db, new FakeLinkedAccounts(), NullLogger<TokenRefreshService>.Instance);
                var cards = new StudentIdCardService(new ConfigurationBuilder().Build());
                return new StudentIdCardController(new FakeUser(user), Db, new AccountSecretStore(Vault), refresh, factory, cards, Logger)
                {
                    ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
                };
            }
        }

        [Theory]
        [InlineData(typeof(StudentIdCardController))]
        [InlineData(typeof(StatelessStudentIdCardController))]
        public void Every_card_route_requires_a_signed_in_user(Type controller)
        {
            Assert.NotNull(controller.GetCustomAttribute<AuthorizeAttribute>());
            Assert.Null(controller.GetCustomAttribute<AllowAnonymousAttribute>());
            Assert.All(controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly), m => Assert.Null(m.GetCustomAttribute<AllowAnonymousAttribute>()));
        }

        [Fact]
        public async Task Someone_elses_account_is_not_found_and_schulnetz_is_never_called()
        {
            var env = new Env();
            var controller = env.Controller(Stranger);

            var result = await controller.Get(env.AccountId, default);

            Assert.IsType<NotFoundResult>(result);
            Assert.Empty(env.Handler.Paths);
            Assert.Equal("no-store, private", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public async Task The_owner_gets_the_card_without_caching()
        {
            var env = new Env();
            var controller = env.Controller(Owner);

            var result = Assert.IsType<OkObjectResult>(await controller.Get(env.AccountId, default));

            var card = Assert.IsType<StudentIdCardResponse>(result.Value);
            Assert.Equal("mobile", card.Source);
            Assert.Contains("data:image/png;base64,AAAA", card.Html);
            Assert.Equal("no-store, private", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public async Task A_school_without_the_mobile_report_gets_the_card_from_the_web_page()
        {
            var env = new Env();
            env.Handler.MobileStatus = HttpStatusCode.NotFound;

            var result = Assert.IsType<OkObjectResult>(await env.Controller(Owner).Get(env.AccountId, default));

            Assert.Equal("web", Assert.IsType<StudentIdCardResponse>(result.Value).Source);
            Assert.Contains("/api/websession/scrape", env.Handler.Paths);
        }

        [Fact]
        public async Task An_account_without_sign_in_secrets_is_signin_expired()
        {
            var env = new Env(withSecrets: false);
            var controller = env.Controller(Owner);

            var result = Assert.IsType<ObjectResult>(await controller.Get(env.AccountId, default));

            Assert.Equal(409, result.StatusCode);
            Assert.Contains("signin_expired", JsonSerializer.Serialize(result.Value));
            Assert.Equal("no-store, private", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public async Task The_card_is_neither_logged_nor_stored()
        {
            var env = new Env();
            env.Handler.MobileStatus = HttpStatusCode.NotFound;
            var before = env.Vault.Get($"account:{env.AccountId}");

            await env.Controller(Owner).Get(env.AccountId, default);

            Assert.DoesNotContain(env.Logger.Lines, l => l.Contains("base64"));
            Assert.Equal(before, env.Vault.Get($"account:{env.AccountId}"));
            Assert.Equal(1, env.Vault.Count);
            Assert.DoesNotContain("base64", JsonSerializer.Serialize(await env.Db.Accounts.AsNoTracking().ToListAsync()));
        }
    }
}
