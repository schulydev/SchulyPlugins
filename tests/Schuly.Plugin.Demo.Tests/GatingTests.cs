using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
using Schuly.Plugin.Demo.Services;
using Xunit;

namespace Schuly.Plugin.Demo.Tests
{
    public class GatingTests
    {
        private static readonly Guid Reviewer = Guid.Parse("0a6c1f4e-8a43-4a52-9f7e-3d6f1b2c9e10");
        private static readonly Guid SecondReviewer = Guid.Parse("5b1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e");

        private static (ServiceProvider Provider, FakeSchoolData Data, FakeFileStore Files) Host(params (string Key, string? Value)[] config)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value))).Build();
            var data = new FakeSchoolData();
            var files = new FakeFileStore();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton<TimeProvider>(new FixedTime(new DateTimeOffset(2026, 11, 11, 9, 0, 0, TimeSpan.Zero)));
            services.AddScoped<ISchoolData>(_ => data);
            services.AddScoped<IPluginFileStore>(_ => files);
            new DemoPlugin().ConfigureServices(services, new PluginServiceContext("Host=unused", configuration));
            return (services.BuildServiceProvider(), data, files);
        }

        private static Task RunAsync(ServiceProvider provider) =>
            provider.GetServices<IPluginBackgroundTask>().Single().ExecuteAsync(provider.CreateScope().ServiceProvider, CancellationToken.None);

        [Fact]
        public void Nothing_is_offered_to_users()
        {
            var (provider, _, _) = Host();
            using var scope = provider.CreateScope();

            Assert.Empty(scope.ServiceProvider.GetServices<IPluginLogin>());
            Assert.Empty(scope.ServiceProvider.GetServices<ILinkProvider>());
            Assert.Empty(scope.ServiceProvider.GetServices<IPluginEventHandler<PluginUserPurgeEvent>>());
        }

        [Fact]
        public void No_endpoints_are_mapped()
        {
            var app = WebApplication.CreateBuilder().Build();
            new DemoPlugin().ConfigureEndpoints(app);
            Assert.Empty(((IEndpointRouteBuilder)app).DataSources.SelectMany(d => d.Endpoints));
        }

        [Fact]
        public async Task Without_configured_accounts_nothing_is_written()
        {
            var (provider, data, files) = Host();
            await RunAsync(provider);

            Assert.Empty(data.Students);
            Assert.Empty(data.Synced);
            Assert.Empty(files.Saved);
        }

        [Fact]
        public async Task Only_configured_accounts_get_demo_data()
        {
            var (provider, data, _) = Host(($"{DemoAccounts.Key}:0", Reviewer.ToString()), ($"{DemoAccounts.Key}:1", "not-a-guid"));
            await RunAsync(provider);

            Assert.Equal([Reviewer], data.Students);
            Assert.Equal(["absences", "agenda", "documents", "exams", "grades", "reports"], data.Synced.Keys.Order());
        }

        [Fact]
        public async Task Documents_are_uploaded_once()
        {
            var (provider, data, files) = Host(($"{DemoAccounts.Key}:0", Reviewer.ToString()));
            await RunAsync(provider);
            await RunAsync(provider);

            Assert.Equal(3, files.Saved.Count);
            Assert.All(files.Saved, f => Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString(f.Content, 0, 4)));
            Assert.Equal(3, ((IReadOnlyCollection<DocumentRecord>)data.Synced["documents"]).Count);
        }

        [Theory]
        [InlineData(null, 0)]
        [InlineData("", 0)]
        [InlineData("nope", 0)]
        [InlineData("00000000-0000-0000-0000-000000000000", 0)]
        [InlineData("0a6c1f4e-8a43-4a52-9f7e-3d6f1b2c9e10", 1)]
        [InlineData("0a6c1f4e-8a43-4a52-9f7e-3d6f1b2c9e10, 5b1d2e3f-4a5b-4c6d-8e7f-901a2b3c4d5e", 2)]
        [InlineData("0a6c1f4e-8a43-4a52-9f7e-3d6f1b2c9e10,0a6c1f4e-8a43-4a52-9f7e-3d6f1b2c9e10", 1)]
        public void User_ids_are_read_from_a_comma_separated_value(string? value, int expected)
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection([new(DemoAccounts.Key, value)]).Build();
            Assert.Equal(expected, DemoAccounts.UserIds(configuration).Count);
        }

        [Fact]
        public void User_ids_are_read_from_a_list()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection([new($"{DemoAccounts.Key}:0", Reviewer.ToString()), new($"{DemoAccounts.Key}:1", SecondReviewer.ToString())]).Build();
            Assert.Equal([Reviewer, SecondReviewer], DemoAccounts.UserIds(configuration));
        }
    }
}
