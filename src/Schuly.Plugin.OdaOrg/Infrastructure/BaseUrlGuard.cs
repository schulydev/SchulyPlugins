using System.Net;
using System.Net.Sockets;

namespace Schuly.Plugin.OdaOrg.Infrastructure
{
    /// <summary>
    /// Strict allow-list for caller- or account-supplied school portal URLs. <see cref="IsAllowed"/> is the cheap
    /// synchronous pre-check on the URL string; <see cref="IsPublicAddress"/> is the connect-time check used by
    /// <see cref="GuardedHttp"/> on every resolved address.
    /// </summary>
    public static class BaseUrlGuard
    {
        private static readonly string[] BlockedSuffixes = [".localhost", ".local", ".internal", ".lan", ".home.arpa"];

        /// <summary>https only, no userinfo, port 443, a multi-label DOdaOrg name (no IP literals, no single-label/Docker names).</summary>
        public static bool IsAllowed(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttps) return false;
            if (!string.IsNullOrEmpty(uri.UserInfo)) return false;
            if (uri.Port != 443) return false;
            if (uri.HostNameType != UriHostNameType.Dns) return false; // rejects IPv4 / IPv6 literals

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (host.Length == 0 || host.Length > 253) return false;
            if (IPAddress.TryParse(host, out _)) return false;
            if (!host.Contains('.')) return false; // single-label: localhost, postgres, seaweedfs, schulware, ...
            if (host == "localhost") return false;
            foreach (var suffix in BlockedSuffixes)
                if (host.EndsWith(suffix, StringComparison.Ordinal)) return false;
            return true;
        }

        /// <summary>True only for globally routable unicast addresses.</summary>
        public static bool IsPublicAddress(IPAddress ip)
        {
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var b = ip.GetAddressBytes();

            if (ip.AddressFamily == AddressFamily.InterNetwork) return IsPublicV4(b[0], b[1], b[2]);
            if (ip.AddressFamily != AddressFamily.InterNetworkV6) return false;

            // Only 2000::/3 (global unicast) is allowed: this drops ::/128, ::1, ::/96 (IPv4-compatible),
            // 64:ff9b::/96 (NAT64), fe80::/10, fec0::/10, fc00::/7 and ff00::/8.
            if ((b[0] & 0xe0) != 0x20) return false;
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x00 && b[3] == 0x00) return false; // 2001::/32 Teredo
            if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false; // 2001:db8::/32 documentation
            if (b[0] == 0x20 && b[1] == 0x02) return IsPublicV4(b[2], b[3], b[4]); // 2002::/16 6to4 embeds an IPv4
            return true;
        }

        private static bool IsPublicV4(byte a, byte b, byte c)
        {
            if (a is 0 or 10 or 127) return false;                    // 0/8, 10/8, loopback
            if (a == 100 && b >= 64 && b <= 127) return false;        // CGNAT 100.64/10
            if (a == 169 && b == 254) return false;                   // link-local incl. 169.254.169.254
            if (a == 172 && b >= 16 && b <= 31) return false;         // 172.16/12
            if (a == 192 && b == 168) return false;                   // 192.168/16
            if (a == 192 && b == 0 && c is 0 or 2) return false;      // 192.0.0/24, 192.0.2/24
            if (a == 198 && b is 18 or 19) return false;              // benchmarking 198.18/15
            if (a == 198 && b == 51 && c == 100) return false;        // 198.51.100/24
            if (a == 203 && b == 0 && c == 113) return false;         // 203.0.113/24
            if (a >= 224) return false;                               // multicast 224/4, reserved 240/4, broadcast
            return true;
        }
    }
}
