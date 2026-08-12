namespace Schuly.Plugin.Shared.Provisioning
{
    public static class InstanceUrl
    {
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

        public static string? LogoFor(string? baseUrl) =>
            Uri.TryCreate((baseUrl ?? "").Trim(), UriKind.Absolute, out var u)
                ? $"https://icons.duckduckgo.com/ip3/{u.Host}.ico"
                : null;
    }
}
