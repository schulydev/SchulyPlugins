using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>Per-school absence reasons from <c>AbsenceReasons</c> in the plugin config, merged with the reasons the student already used.</summary>
    public static class AbsenceReasons
    {
        /// <summary>The list configured for the school's host (case-insensitive), or <c>AbsenceReasons:Default</c> when the school has none.</summary>
        public static IReadOnlyList<string> Configured(IConfiguration configuration, string? schulnetzBaseUrl)
        {
            var host = HostOf(schulnetzBaseUrl);
            var school = host is null
                ? null
                : configuration.GetSection("AbsenceReasons:Schools").GetChildren().FirstOrDefault(c => string.Equals(c.Key, host, StringComparison.OrdinalIgnoreCase));
            return ReadList(school ?? configuration.GetSection("AbsenceReasons:Default"));
        }

        /// <summary>The configured reasons in order, then used reasons not yet in it (case-insensitive, first-seen spelling), most recently used first.</summary>
        public static IReadOnlyList<string> Merge(IEnumerable<string> configured, IEnumerable<AbsenceRow> rows)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var reason in configured)
                if (!string.IsNullOrWhiteSpace(reason) && seen.Add(reason.Trim())) result.Add(reason.Trim());

            var used = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.Reason))
                .OrderByDescending(r => r.DateFrom, StringComparer.Ordinal)
                .ThenByDescending(r => r.StudentTimestamp, StringComparer.Ordinal);
            foreach (var row in used)
                if (seen.Add(row.Reason!.Trim())) result.Add(row.Reason.Trim());
            return result;
        }

        private static string? HostOf(string? baseUrl)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) return null;
            var text = baseUrl.Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)) return uri.Host;
            return Uri.TryCreate("https://" + text, UriKind.Absolute, out uri) ? uri.Host : null;
        }

        private static List<string> ReadList(IConfigurationSection section) =>
            section.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
    }
}
