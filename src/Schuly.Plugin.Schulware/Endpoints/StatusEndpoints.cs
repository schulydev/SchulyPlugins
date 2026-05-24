using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Schuly.Plugin.Schulware.Endpoints
{
    internal static class StatusEndpoints
    {
        public static IEndpointRouteBuilder MapSchulwareStatus(this IEndpointRouteBuilder endpoints, string pluginName, string version)
        {
            endpoints.MapGet("/api/plugins/schulware/status", () =>
                Results.Ok(new { Status = "Active", Plugin = pluginName, Version = version })
            ).AllowAnonymous();

            return endpoints;
        }
    }
}
