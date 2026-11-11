using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Demo.Services;

namespace Schuly.Plugin.Demo
{
    /// <summary>
    /// Fills the Schuly accounts listed under <c>Demo:UserIds</c> with a fictional school, for app store review.
    /// It registers no school system, no link provider and no endpoints, so nobody else can see or reach it.
    /// </summary>
    public class DemoPlugin : ISchulyPlugin
    {
        public const string PluginName = "Demo";

        public static readonly string PluginVersion = typeof(DemoPlugin).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        public string Name => PluginName;
        public string Version => PluginVersion;

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<DemoSyncTask>();
            services.AddSingleton<IPluginBackgroundTask>(sp => sp.GetRequiredService<DemoSyncTask>());
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
