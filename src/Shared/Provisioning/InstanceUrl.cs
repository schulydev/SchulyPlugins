namespace Schuly.Plugin.Shared.Provisioning
{
    /// <summary>
    /// Derives a stable per-school key and logo from the portal instance URL. The
    /// instance URL (the actual school portal the user connects to) is the only
    /// uniform, unspoofable per-school identifier the plugins have - unlike the
    /// user-typed display name.
    /// </summary>
    public static class InstanceUrl
    {
        /// <summary>
        /// Stable per-instance key: scheme + lowercased host[:port] + trimmed path.
        /// Only the host is lower-cased and the trailing slash dropped, so the result
        /// matches the (already trailing-slash-trimmed) URLs the plugins already store
        /// in School.Website - no migration needed. The path is kept so shared-host
        /// portals (e.g. www.schul-netz.com/&lt;school&gt;) stay distinct schools.
        /// </summary>
        public static string Canonical(string? baseUrl)
        {
            var raw = (baseUrl ?? "").Trim();
            if (!Uri.TryCreate(raw, UriKind.Absolute, out var u))
                return raw.TrimEnd('/');
            var port = u.IsDefaultPort ? "" : $":{u.Port}";
            return $"{u.Scheme.ToLowerInvariant()}://{u.Host.ToLowerInvariant()}{port}{u.AbsolutePath.TrimEnd('/')}";
        }

        public static string Host(string? baseUrl) =>
            Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var u) ? u.Host : (baseUrl ?? "").Trim();

        /// <summary>Public favicon resolver keyed by host - a real per-school icon, no auth.</summary>
        public static string? LogoFor(string? baseUrl) =>
            Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var u)
                ? $"https://icons.duckduckgo.com/ip3/{u.Host}.ico"
                : null;
    }
}
