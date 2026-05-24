using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Endpoints;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware
{
    /// <summary>
    /// Schulware plugin entry point. Modeled after an ASP.NET Core composition root:
    /// the plugin file itself is slim — DI registration lives here, route mapping is
    /// delegated to <c>Endpoints/*EndpointsExtensions</c>, and the work happens in
    /// <c>Services/</c>, <c>Data/</c>, <c>Infrastructure/</c>, <c>Dtos/</c>.
    /// </summary>
    public class SchulwarePlugin : ISchulyPlugin
    {
        public string Name => "Schulware Integration";
        public string Version => "2.0.0";

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            // Hard requirement: a Schuly.Plugin.Schulware.yml must sit in the
            // backend's plugins-config directory with at least SchulwareApi.BaseUrl
            // populated. Without it we have nowhere to talk to, so refuse to load
            // rather than fail silently at sync time.
            var baseUrl = context.Configuration["SchulwareApi:BaseUrl"];
            if (string.IsNullOrWhiteSpace(baseUrl))
                throw new InvalidOperationException(
                    "Schulware plugin: missing config. Drop a Schuly.Plugin.Schulware.yml " +
                    "into the backend's plugins-config directory with at least " +
                    "SchulwareApi.BaseUrl set. See the source repo for the example schema.");

            services.AddDbContext<SchulwareDbContext>(options => options.UseNpgsql(context.ConnectionString));
            services.AddSingleton<IPluginBackgroundTask, SchulwareSyncTask>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints
                .MapSchulwareStatus(Name, Version)
                .MapSchulwareAccounts()
                .MapSchulwareOAuth()
                .MapSchulwareSync();
        }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            // Apply pending EF Core migrations (creates the DB on first run, applies schema deltas after).
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
