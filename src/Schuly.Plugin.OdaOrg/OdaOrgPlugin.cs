using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.OdaOrg.Data;
using Schuly.Plugin.OdaOrg.Infrastructure;
using Schuly.Plugin.OdaOrg.Services;

namespace Schuly.Plugin.OdaOrg
{
    public class OdaOrgPlugin : ISchulyPlugin
    {
        public const string PluginName = "OdaOrg Integration";

        /// <summary>Reported version — derived from the assembly (csproj &lt;Version&gt;) so it never drifts.</summary>
        public static readonly string PluginVersion =
            typeof(OdaOrgPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            services.AddDbContext<OdaOrgDbContext>(options => options.UseNpgsql(context.ConnectionString));

            services.AddSingleton<OdaOrgSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<OdaOrgSyncTask>());

            services.AddScoped<OdaOrgScraper>();
            services.AddScoped<ProvisioningService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AgendaSyncService>();

            // Credentials live in this plugin's isolated, in-memory vault (keyed by
            // the plugin name by the host) instead of the database.
            services.AddScoped(sp => new OdaOrgSecretStore(
                sp.GetRequiredKeyedService<IPluginVault>(PluginName)));

            services.AddScoped<IPluginLogin, OdaOrgLogin>();
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
