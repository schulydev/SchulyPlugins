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
        // The host keys each plugin's isolated vault by the plugin's Name, so the
        // name has to be a constant to use it with [FromKeyedServices].
        public const string PluginName = "Example Plugin";

        public string Name => PluginName;

        // Single source of truth: the csproj <Version>, surfaced via the assembly.
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

            // Demonstrates the per-plugin secret vault: resolve it with
            // [FromKeyedServices(PluginName)] and the plugin gets its own
            // cryptographically-isolated store. Values are encrypted in memory
            // (AES-GCM) with a key the host generates at startup, so they're never
            // sitting in the backing store as plaintext, and no other plugin can
            // read them. The vault is in-memory only — values don't survive a restart.
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
