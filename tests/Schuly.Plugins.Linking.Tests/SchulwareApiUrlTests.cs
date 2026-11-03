using System.Net;
using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Schulware.Controllers;
using Schuly.Plugin.Schulware.Dtos;
using Schuly.Plugin.Schulware.Infrastructure;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SchulwareApiUrlTests
    {
        private sealed class RecordingFactory : IHttpClientFactory
        {
            public int Created;

            public HttpClient CreateClient(string name)
            {
                Created++;
                return new HttpClient(new RefusingHandler());
            }
        }

        private sealed class RefusingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw new InvalidOperationException($"Nothing should have been sent to {request.RequestUri}");
        }

        private static IConfiguration Config(string? baseUrl) => new ConfigurationBuilder().AddInMemoryCollection([new KeyValuePair<string, string?>("SchulwareApi:BaseUrl", baseUrl)]).Build();

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("  ")]
        public void Missing_setting_throws_instead_of_using_a_default(string? value) => Assert.Throws<InvalidOperationException>(() => SchulwareApiUrl.Required(Config(value)));

        [Fact]
        public void Configured_setting_is_returned() => Assert.Equal("http://schulware:8000", SchulwareApiUrl.Required(Config("http://schulware:8000")));

        [Fact]
        public async Task Stateless_data_fails_without_sending_the_token_when_the_setting_is_missing()
        {
            var factory = new RecordingFactory();
            var controller = new StatelessDataController(factory, Config(null), NullLogger<StatelessDataController>.Instance) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
            controller.Request.Headers["X-Plugin-Token"] = "token";
            controller.Request.Headers["X-Provider-Base-Url"] = "https://school.example.ch";

            var result = await controller.Grades();

            Assert.Equal((int)HttpStatusCode.BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(0, factory.Created);
        }

        [Fact]
        public async Task Stateless_login_fails_without_sending_the_password_when_the_setting_is_missing()
        {
            var factory = new RecordingFactory();
            var controller = new StatelessAuthController(factory, Config(null), NullLogger<StatelessAuthController>.Instance);

            var result = await controller.Login(new StatelessLoginRequest("https://school.example.ch", "a@school.ch", "secret", null));

            Assert.Equal((int)HttpStatusCode.BadGateway, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.Equal(0, factory.Created);
        }

        [Fact]
        public void No_code_names_an_external_host()
        {
            var assembly = typeof(SchulwareApiUrl).Assembly;
            var strings = assembly.GetTypes().SelectMany(t => t.GetFields(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)).Where(f => f.IsLiteral && f.FieldType == typeof(string)).Select(f => (string)f.GetRawConstantValue()!);
            Assert.DoesNotContain(strings, s => s.Contains("pianonic.ch", StringComparison.OrdinalIgnoreCase));
        }
    }
}
