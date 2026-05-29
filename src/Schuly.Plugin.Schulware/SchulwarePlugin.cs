using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;

namespace Schuly.Plugin.Schulware
{
    /// <summary>
    /// Schulware plugin composition root. DI registration happens here;
    /// HTTP routes live in <c>Controllers/*Controller.cs</c> (regular ASP.NET
    /// controllers — the host registers this assembly as an MVC
    /// ApplicationPart, so they're discovered automatically).
    /// </summary>
    public class SchulwarePlugin : ISchulyPlugin
    {
        public const string PluginName = "Schulware Integration";
        public const string PluginVersion = "2.3.0";

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

            // Sync task is the public entry point; the work is split across
            // focused scoped services so each file stays small and testable.
            services.AddSingleton<SchulwareSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<SchulwareSyncTask>());
            services.AddScoped<TokenRefreshService>();
            services.AddScoped<GradesSyncService>();
            services.AddScoped<AbsencesSyncService>();
            services.AddScoped<AgendaSyncService>();
            services.AddScoped<SchoolProvisioningService>();
            services.AddScoped<OAuthCallbackService>();
        }

        // Routes live in Controllers/, discovered via MVC ApplicationPart registration.
        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<SchulwareDbContext>();
            // Apply pending EF Core migrations (creates the DB on first run, applies schema deltas after).
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
