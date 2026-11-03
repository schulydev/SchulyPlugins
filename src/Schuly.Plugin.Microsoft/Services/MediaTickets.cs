using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>What one media element may play: one file of one linked account of one user.</summary>
    public sealed record MediaTicket(Guid UserId, Guid AccountId, string Path, byte[] SecretHash);

    /// <summary>
    /// A <c>&lt;video&gt;</c> can't send the session's bearer token, and no token may go in a URL. So a signed-in call opens a ticket for one file: the URL holds only a handle that is useless alone,
    /// and the secret comes back as an HttpOnly, SameSite=Strict cookie scoped to that URL. Held in memory only, for minutes after the last use; a restart just means the viewer asks again.
    /// </summary>
    public sealed class MediaTickets
    {
        public const string CookieName = "schuly_media";
        public static readonly TimeSpan IdleLife = TimeSpan.FromMinutes(15);
        public static readonly TimeSpan MaxLife = TimeSpan.FromHours(2);
        private const int MaxTickets = 10_000;

        private readonly MemoryCache tickets = new(new MemoryCacheOptions { SizeLimit = MaxTickets });

        public (string Handle, string Secret) Open(Guid userId, Guid accountId, string path)
        {
            var handle = Base64Url(RandomNumberGenerator.GetBytes(24));
            var secret = Base64Url(RandomNumberGenerator.GetBytes(32));
            tickets.Set(handle, new MediaTicket(userId, accountId, path, Hash(secret)), new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = IdleLife, AbsoluteExpirationRelativeToNow = MaxLife });
            return (handle, secret);
        }

        /// <summary>The ticket, only when both the handle and the secret match.</summary>
        public MediaTicket? Find(string? handle, string? secret)
        {
            if (string.IsNullOrEmpty(handle) || string.IsNullOrEmpty(secret))
                return null;
            return tickets.TryGetValue(handle, out MediaTicket? ticket) && ticket is not null && CryptographicOperations.FixedTimeEquals(ticket.SecretHash, Hash(secret)) ? ticket : null;
        }

        private static byte[] Hash(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes(secret));

        private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
