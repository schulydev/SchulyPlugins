using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Abstractions.Linking;
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

            services.AddScoped<PendingLinkStore>();
            services.AddScoped<MicrosoftServiceClient>();
            services.AddScoped<IMicrosoftSignInApi>(sp => sp.GetRequiredService<MicrosoftServiceClient>());
            services.AddScoped<MicrosoftAccountLinker>();
            services.AddSingleton(new PasswordAttemptLimiter(TimeProvider.System));
            services.AddScoped<ILinkProvider, MicrosoftLinkProvider>();
            services.AddScoped<MicrosoftAccountReader>();
            services.AddScoped<ScheduledMessageStore>();
            services.AddSingleton<IPluginBackgroundTask, ScheduledMessageTask>();
            services.AddScoped<IPluginEventHandler<PluginUserPurgeEvent>, MicrosoftUserPurgeHandler>();
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) { }

        public Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
