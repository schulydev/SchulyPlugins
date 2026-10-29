using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>Validation for ids that become path segments upstream. A pattern alone is not enough: ids may contain ".", and a "." or ".." segment is collapsed by HttpClient (and by Graph), retargeting the call.</summary>
    public static class PathIds
    {
        /// <summary>True when <paramref name="id"/> matches <paramref name="pattern"/> and has no dot segment (".", "..", or ".." anywhere).</summary>
        public static bool IsSafe(Regex pattern, string? id) =>
            id is not null && pattern.IsMatch(id) && !HasDotSegment(id);

        public static bool HasDotSegment(string id) =>
            id.Contains("..", StringComparison.Ordinal) || id.Split('/', '\\').Any(segment => segment == ".");

        /// <summary>Decodes a catch-all route value exactly once. The server already decoded the path except "%2F", so only that escape is turned back into "/"; any other "%" left over (a double-encoded id) then fails the id pattern.</summary>
        public static string DecodeCatchAll(string raw) =>
            raw.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);

        /// <summary>The absolute upstream URI for <c>api/{path}</c>, refusing any path the URI parser rewrote (dot segments collapsed, decoded "%2E", a changed segment count).</summary>
        public static Uri Upstream(Uri baseUri, string path)
        {
            var prefix = baseUri.AbsolutePath.TrimEnd('/') + "/api/";
            var uri = new Uri(baseUri, "api/" + path);
            var rawPath = path.Split('?', 2)[0];
            var expected = prefix.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + rawPath.Split('/').Length;
            var actual = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length + (uri.AbsolutePath.EndsWith('/') ? 1 : 0);
            if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal) || actual != expected || HasDotSegment(Uri.UnescapeDataString(uri.AbsolutePath)))
                throw new ArgumentException("The upstream path was rewritten; refusing the call.", nameof(path));
            return uri;
        }
    }
}
