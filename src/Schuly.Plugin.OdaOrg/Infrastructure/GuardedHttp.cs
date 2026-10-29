using System.Net;
using System.Net.Sockets;

namespace Schuly.Plugin.OdaOrg.Infrastructure
{
    /// <summary>
    /// Builds <see cref="HttpClient"/>s for requests to a caller- or account-supplied school URL. The handler resolves the host
    /// itself, refuses to connect if ANY resolved address is non-public, and connects to the vetted address (so DOdaOrg rebinding between
    /// check and connect cannot swap it). Redirects are never followed automatically and no proxy is used.
    /// </summary>
    public static class GuardedHttp
    {
        public static HttpClient Create(CookieContainer? cookies = null, int timeoutSeconds = 20)
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                UseProxy = false,
                UseCookies = cookies is not null,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionLifetime = TimeSpan.FromMinutes(1),
                ConnectCallback = ConnectAsync,
            };
            if (cookies is not null) handler.CookieContainer = cookies;
            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = TimeSpan.FromSeconds(timeoutSeconds),
                MaxResponseContentBufferSize = 10 * 1024 * 1024,
            };
        }

        private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            var host = context.DnsEndPoint.Host;
            var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct);
            if (addresses.Length == 0 || addresses.Any(a => !BaseUrlGuard.IsPublicAddress(a)))
                throw new HttpRequestException("Target address is not allowed.");

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
