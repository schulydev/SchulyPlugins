using Microsoft.Extensions.Configuration;

namespace Schuly.Plugin.Shared
{
    /// <summary>
    /// On a server with <c>SignUp:SwitzerlandOnly</c>, new connections only reach school systems in Switzerland: a <c>.ch</c> host, or a shared host that only serves Swiss
    /// schools. This sits on top of the plugins' BaseUrlGuard, which is an SSRF guard and takes any public https name. It only runs when a connection starts, so accounts
    /// that are already linked keep syncing and refreshing.
    /// </summary>
    internal static class SwissInstances
    {
        public const string Refused = "Schuly only supports schools in Switzerland on this server.";

        // Centerboard's shared Schulnetz host. Every school listed on it in SchulnetzSchools is in a Swiss canton.
        private static readonly string[] SharedHosts = ["www.schul-netz.com", "schul-netz.com"];

        public static bool Limited(IConfiguration? configuration) => string.Equals(configuration?["SignUp:SwitzerlandOnly"], "true", StringComparison.OrdinalIgnoreCase);

        public static bool IsSwiss(string? url)
        {
            if (!Uri.TryCreate((url ?? "").Trim(), UriKind.Absolute, out var uri))
                return false;

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            return host.EndsWith(".ch", StringComparison.Ordinal) || SharedHosts.Contains(host);
        }

        public static bool Allows(IConfiguration? configuration, string? url) => !Limited(configuration) || IsSwiss(url);
    }
}
