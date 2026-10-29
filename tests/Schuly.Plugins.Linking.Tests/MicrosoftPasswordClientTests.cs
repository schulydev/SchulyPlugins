using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    /// <summary>The SchulyMicrosoft password API mapping: headers on every route, status codes and the snake_case body.</summary>
    public class MicrosoftPasswordClientTests
    {
        private static readonly Guid User = Guid.NewGuid();

        private sealed class Handler : HttpMessageHandler
        {
            public readonly List<HttpRequestMessage> Requests = new();
            public HttpStatusCode Status = HttpStatusCode.OK;
            public string Body = "{}";

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") });
            }
        }

        private sealed class Factory(Handler handler) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name) => new(handler, false);
        }

        private static (MicrosoftServiceClient Client, Handler Handler) Create(string? key = "svc-key")
        {
            var handler = new Handler();
            var values = new Dictionary<string, string?> { ["MicrosoftApi:BaseUrl"] = "http://ms.test" };
            if (key is not null) values["MicrosoftApi:ServiceKey"] = key;
            return (new MicrosoftServiceClient(new Factory(handler), new ConfigurationBuilder().AddInMemoryCollection(values).Build()), handler);
        }

        [Fact]
        public async Task EveryPasswordRoute_SendsTheServiceKeyAndUserId()
        {
            var (client, handler) = Create();
            handler.Body = "{\"status\":\"complete\"}";
            await client.StartPasswordAsync(User, "a@b.ch", "pw", default);
            await client.ChooseMethodAsync(User, "id1", "push", default);
            await client.SubmitOtpAsync(User, "id1", "123456", default);
            await client.GetPasswordStatusAsync(User, "id1", default);
            await client.CancelPasswordAsync(User, "id1", default);

            Assert.Equal(5, handler.Requests.Count);
            Assert.All(handler.Requests, r =>
            {
                Assert.Equal("svc-key", r.Headers.GetValues("X-Schuly-Service-Key").Single());
                Assert.Equal(User.ToString(), r.Headers.GetValues("X-Schuly-User-Id").Single());
            });
            Assert.Equal(["/api/auth/password/start", "/api/auth/password/id1/method", "/api/auth/password/id1/otp", "/api/auth/password/id1", "/api/auth/password/id1"], handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
            Assert.Equal(HttpMethod.Delete, handler.Requests[4].Method);
        }

        [Fact]
        public async Task WithoutAServiceKey_NothingIsSent()
        {
            var (client, handler) = Create(key: null);
            Assert.False(client.PasswordSignInConfigured);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.StartPasswordAsync(User, "a@b.ch", "pw", default));
            Assert.Empty(handler.Requests);
        }

        [Fact]
        public async Task ParsesTheSnakeCaseBody()
        {
            var (client, handler) = Create();
            handler.Body = "{\"sign_in_id\":\"s1\",\"status\":\"push_pending\",\"number\":\"42\",\"expires_in\":75,\"methods\":[\"push\",\"otp\"],\"attempts_left\":3}";
            var state = await client.ChooseMethodAsync(User, "s1", "push", default);
            Assert.Equal(("push_pending", "s1", "42", 75, 3), (state.Status, state.SignInId, state.Number, state.ExpiresInSeconds, state.AttemptsLeft));
            Assert.Equal(["push", "otp"], state.Methods);

            handler.Body = "{\"sign_in_id\":\"s1\",\"status\":\"complete\",\"refresh_token\":\"rt\",\"account\":{\"id\":\"oid\",\"email\":\"a@b.ch\",\"name\":\"A\"}}";
            var done = await client.GetPasswordStatusAsync(User, "s1", default);
            Assert.Equal(("rt", "oid", "a@b.ch"), (done.RefreshToken, done.MicrosoftUserId, done.Email));
        }

        [Theory]
        [InlineData(HttpStatusCode.Unauthorized, "failed")]
        [InlineData(HttpStatusCode.ServiceUnavailable, "failed")]
        [InlineData(HttpStatusCode.NotFound, "expired")]
        [InlineData(HttpStatusCode.TooManyRequests, "rate_limited")]
        [InlineData(HttpStatusCode.Conflict, "conflict")]
        [InlineData(HttpStatusCode.UnprocessableEntity, "invalid")]
        public async Task HttpStatuses_MapToCodes(HttpStatusCode status, string code)
        {
            var (client, handler) = Create();
            handler.Status = status;
            handler.Body = "{\"detail\":\"x\"}";
            var state = await client.GetPasswordStatusAsync(User, "s1", default);
            Assert.Equal("error", state.Status);
            Assert.Equal(code, state.ErrorCode);
        }

        [Fact]
        public async Task AnErrorBodyKeepsTheServicesCode()
        {
            var (client, handler) = Create();
            handler.Status = HttpStatusCode.OK;
            handler.Body = "{\"status\":\"error\",\"code\":\"invalid_credentials\",\"message\":\"nope\"}";
            var state = await client.StartPasswordAsync(User, "a@b.ch", "pw", default);
            Assert.Equal("invalid_credentials", state.ErrorCode);
        }
    }
}
