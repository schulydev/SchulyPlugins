using System.Net;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    /// <summary>
    /// Regression tests for "a Microsoft account linked during Schulnetz sign-in does not appear at first; a retry shows an
    /// error, then it is visible": the legacy link endpoints must be idempotent and must not lose a pending sign-in.
    /// </summary>
    public class MicrosoftLinkControllerTests
    {
        private static readonly Guid User = Guid.NewGuid();

        private sealed class Handler : HttpMessageHandler
        {
            public int CompleteCalls;
            public int StartCalls;
            public string Oid = "oid-1";
            public string Email = "a@school.ch";
            public bool FailOnce;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path.EndsWith("browser/start"))
                {
                    StartCalls++;
                    return Task.FromResult(Json($"{{\"authorize_url\":\"https://login.microsoftonline.com/a{StartCalls}\",\"redirect_uri\":\"https://r/cb\",\"code_verifier\":\"verifier-{StartCalls}\"}}"));
                }
                CompleteCalls++;
                if (FailOnce)
                {
                    FailOnce = false;
                    throw new HttpRequestException("network");
                }
                return Task.FromResult(Json($"{{\"status\":\"complete\",\"refresh_token\":\"rt\",\"account\":{{\"id\":\"{Oid}\",\"email\":\"{Email}\",\"name\":\"A\"}}}}"));
            }

            private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        private sealed class Factory(Handler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, false);
        }

        private static (LinkController Controller, Handler Handler, FakeLinkedAccounts Accounts) Create()
        {
            var handler = new Handler();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["MicrosoftApi:BaseUrl"] = "http://ms.test" }).Build();
            var accounts = new FakeLinkedAccounts();
            var controller = new LinkController(new FakeUser(User), new MicrosoftAccountLinker(accounts), new PendingLinkStore(new FakeVault()), new MicrosoftServiceClient(new Factory(handler), config));
            return (controller, handler, accounts);
        }

        private static T Body<T>(IActionResult result) where T : class => (T)((OkObjectResult)result).Value!;

        private static string Prop(object o, string name) => o.GetType().GetProperty(name)!.GetValue(o)?.ToString() ?? "";

        [Fact]
        public async Task CompletingTwiceWithTheSameCode_AnswersCompleteBothTimes_WithOneAccount()
        {
            var (c, _, accounts) = Create();
            var start = Body<object>(await c.Start(default));
            var request = new LinkController.CompleteRequest("code-1", null, Prop(start, "sessionId"));

            var first = Body<object>(await c.Complete(request, default));
            var second = Body<object>(await c.Complete(request, default));

            Assert.Equal("complete", Prop(first, "status"));
            Assert.Equal("complete", Prop(second, "status"));
            Assert.Equal(Prop(first, "accountId"), Prop(second, "accountId"));
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task ASecondStart_DoesNotBreakTheFirstSignIn()
        {
            var (c, handler, accounts) = Create();
            var first = Body<object>(await c.Start(default));
            await c.Start(default);

            var done = Body<object>(await c.Complete(new LinkController.CompleteRequest("code-1", null, Prop(first, "sessionId")), default));

            Assert.Equal("complete", Prop(done, "status"));
            Assert.Single(accounts.Rows);
            Assert.Equal(1, handler.CompleteCalls);
        }

        [Fact]
        public async Task OlderApps_WithoutASessionId_UseTheLatestStart()
        {
            var (c, _, accounts) = Create();
            await c.Start(default);
            var done = Body<object>(await c.Complete(new LinkController.CompleteRequest("code-1", null), default));
            Assert.Equal("complete", Prop(done, "status"));
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task ANetworkFailure_KeepsThePendingSignIn_SoTheRetrySucceeds()
        {
            var (c, handler, accounts) = Create();
            var start = Body<object>(await c.Start(default));
            var request = new LinkController.CompleteRequest("code-1", null, Prop(start, "sessionId"));
            handler.FailOnce = true;

            var failed = Body<object>(await c.Complete(request, default));
            Assert.Equal("error", Prop(failed, "status"));
            Assert.Empty(accounts.Rows);

            var retried = Body<object>(await c.Complete(request, default));
            Assert.Equal("complete", Prop(retried, "status"));
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task LinkingTheSameAccountAgain_UpdatesTheExistingRow_EvenIfItWasStoredUnderTheEmail()
        {
            var (c, _, accounts) = Create();
            var legacy = await accounts.SaveAsync(User, new LinkedAccountRecord(LinkedProviders.Microsoft, "a@school.ch", "a@school.ch", "A"));

            var start = Body<object>(await c.Start(default));
            var done = Body<object>(await c.Complete(new LinkController.CompleteRequest("code-2", null, Prop(start, "sessionId")), default));

            Assert.Equal("complete", Prop(done, "status"));
            Assert.Equal(legacy.Id.ToString(), Prop(done, "accountId"));
            Assert.Single(accounts.Rows);
        }

        [Fact]
        public async Task WithoutAnyPendingSignIn_TheAnswerIsStillExpired()
        {
            var (c, _, _) = Create();
            var done = Body<object>(await c.Complete(new LinkController.CompleteRequest("never-started", null), default));
            Assert.Equal("expired", Prop(done, "status"));
        }
    }
}
