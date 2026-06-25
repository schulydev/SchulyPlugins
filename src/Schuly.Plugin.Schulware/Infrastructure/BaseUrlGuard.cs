using System.Net;
using System.Net.Sockets;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>
    /// Guards against SSRF via a caller-supplied portal base URL: only http/https to
    /// a non-internal host is allowed, so the proxy can't be pointed at cloud
    /// metadata, loopback, or private (RFC1918) addresses. Public host names pass
    /// (DNS rebinding is out of scope).
    /// </summary>
    public static class BaseUrlGuard
    {
        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
            if (IPAddress.TryParse(uri.Host, out var ip) && IsInternal(ip)) return false;
            return true;
        }

        private static bool IsInternal(IPAddress ip)
        {
            if (IPAddress.IsLoopback(ip)) return true;
            if (ip.IsIPv6LinkLocal) return true;
            var b = ip.GetAddressBytes();
            if (ip.AddressFamily == AddressFamily.InterNetwork)
                return b[0] is 0 or 10 or 127
                    || (b[0] == 169 && b[1] == 254)
                    || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                    || (b[0] == 192 && b[1] == 168);
            return b.Length == 16 && (b[0] & 0xfe) == 0xfc; // IPv6 unique-local fc00::/7
        }
    }
}
