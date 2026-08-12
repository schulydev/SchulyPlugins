using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware
{
    public class SchulwarePlugin : ISchulyPlugin
    {
        public const string PluginName = "Schulware Integration";

        /// <summary>Reported version — derived from the assembly (csproj &lt;Version&gt;) so it never drifts.</summary>
        public static readonly string PluginVersion =
            typeof(SchulwarePlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

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

            services.AddSingleton<SchulwareSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<SchulwareSyncTask>());
            services.AddScoped<TokenRefreshService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AbsencesSyncService>();
            services.AddScoped<AgendaSyncService>();
            services.AddScoped<DocumentsSyncService>();
            services.AddScoped<VacationsSyncService>();
            services.AddScoped<SchoolProvisioningService>();

            // The account auth secrets live in this plugin's isolated, in-memory
            // vault (keyed by the plugin name by the host) instead of the database.
            services.AddScoped(sp => new AccountSecretStore(
                sp.GetRequiredKeyedService<IPluginVault>(PluginName)));

            services.AddScoped<IPluginLogin, SchulwareLogin>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
