using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Abstractions;
using System.Reflection;

namespace Schuly.Plugin.Example
{
    public class ExamplePlugin : ISchulyPlugin
    {
        public const string PluginName = "Example Plugin";

        public string Name => PluginName;

        public string Version =>
            GetType().Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
            ?? "0.0.0";

        public void ConfigureServices(IServiceCollection services, PluginServiceContext context)
        {
        }

        public void ConfigureEndpoints(IEndpointRouteBuilder endpoints)
        {
            endpoints.MapGet("/api/plugins/example/hello", () =>
            {
                return Results.Ok(new { Message = "Hello from the Example Plugin!", Plugin = Name, Version });
            }).RequireAuthorization();

            endpoints.MapGet("/api/plugins/example/info", () =>
            {
                return Results.Ok(new { Name, Version, Description = "A sample plugin demonstrating the Schuly plugin system." });
            }).AllowAnonymous();

            endpoints.MapGet("/api/plugins/example/vault-demo",
                ([FromKeyedServices(PluginName)] IPluginVault vault) =>
                {
                    vault.Set("demo-secret", "stored encrypted at rest");
                    return Results.Ok(new
                    {
                        stored = true,
                        readBack = vault.Get("demo-secret"),
                        entries = vault.Count,
                    });
                }).RequireAuthorization();
        }

        public Task MigrateAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
