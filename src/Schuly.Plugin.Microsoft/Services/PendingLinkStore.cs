using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>
    /// The plugin vault: a user's Microsoft sign-ins in progress, one entry per sign-in session (its PKCE verifier), so
    /// parallel or retried starts never overwrite each other. Also remembers which account a used authorization
    /// code produced, so a repeated completion with the same code answers with the same account.
    /// </summary>
    public sealed class PendingLinkStore(IPluginVault vault)
    {
        public sealed record PendingLink(string CodeVerifier, DateTime ExpiresAt);

        private static string LinkKey(Guid userId, string sessionId) => $"link:{userId}:{sessionId}";
        private static string LatestKey(Guid userId) => $"link-latest:{userId}";
        private static string DoneKey(Guid userId, string code) => $"link-done:{userId}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)))}";

        public string Save(Guid userId, PendingLink link)
        {
            var sessionId = Guid.NewGuid().ToString("N");
            vault.Set(LinkKey(userId, sessionId), JsonSerializer.Serialize(link));
            vault.Set(LatestKey(userId), sessionId);
            return sessionId;
        }

        /// <summary>The named session, or the user's most recent one when the client sent no session id (older apps).</summary>
        public (string SessionId, PendingLink Link)? Get(Guid userId, string? sessionId)
        {
            if (string.IsNullOrEmpty(sessionId) && !(vault.TryGet(LatestKey(userId), out sessionId) && !string.IsNullOrEmpty(sessionId)))
                return null;
            if (!vault.TryGet(LinkKey(userId, sessionId!), out var json))
                return null;
            var link = JsonSerializer.Deserialize<PendingLink>(json);
            return link is null ? null : (sessionId!, link);
        }

        public void Remove(Guid userId, string sessionId)
        {
            vault.Remove(LinkKey(userId, sessionId));
            if (vault.TryGet(LatestKey(userId), out var latest) && latest == sessionId)
                vault.Remove(LatestKey(userId));
        }

        /// <summary>Drops the user's most recent session (used when the user is purged).</summary>
        public void RemoveLatest(Guid userId)
        {
            if (vault.TryGet(LatestKey(userId), out var latest) && !string.IsNullOrEmpty(latest))
                Remove(userId, latest);
        }

        public void RememberCompleted(Guid userId, string code, Guid accountId) => vault.Set(DoneKey(userId, code), accountId.ToString());

        public Guid? CompletedAccount(Guid userId, string code) =>
            vault.TryGet(DoneKey(userId, code), out var id) && Guid.TryParse(id, out var guid) ? guid : null;
    }
}
