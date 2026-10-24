using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Data;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;

namespace Schuly.Plugin.Microsoft
{
    public class MicrosoftPlugin : ISchulyPlugin
    {
        public const string PluginName = "Microsoft 365";

        public static readonly string PluginVersion =
            typeof(MicrosoftPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            if (string.IsNullOrWhiteSpace(context.Configuration["MicrosoftApi:BaseUrl"]))
                throw new InvalidOperationException(
                    "Microsoft plugin: missing config. Drop a Schuly.Plugin.Microsoft.yml into the backend's " +
                    "plugins-config directory with MicrosoftApi.BaseUrl pointing at a SchulyMicrosoft instance.");

            services.AddDbContext<MicrosoftDbContext>(options => options.UseNpgsql(context.ConnectionString));
            services.AddScoped<MicrosoftTokenStore>();
            services.AddScoped<MicrosoftServiceClient>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public async Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            using var scope = serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MicrosoftDbContext>();
            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}
