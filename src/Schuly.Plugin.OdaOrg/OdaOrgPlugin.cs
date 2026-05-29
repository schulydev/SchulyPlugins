using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Services;

namespace Schuly.Plugin.OdaOrg
{
    /// <summary>
    /// OdaOrg plugin composition root. Stores connection credentials and runs a
    /// periodic scrape into the main Schuly DB. HTTP routes live in
    /// <c>Controllers/*Controller.cs</c> (MVC ApplicationPart, auto-discovered).
    /// </summary>
    public class OdaOrgPlugin : ISchulyPlugin
    {
        public const string PluginName = "OdaOrg Integration";
        public const string PluginVersion = "1.1.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            services.AddDbContext<OdaOrgDbContext>(options => options.UseNpgsql(context.ConnectionString));

            // Auto-refresh: the host runs this background task on its Interval.
            services.AddSingleton<OdaOrgSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<OdaOrgSyncTask>());

            services.AddScoped<OdaOrgScraper>();
            services.AddScoped<ProvisioningService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AgendaSyncService>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OdaOrgDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
