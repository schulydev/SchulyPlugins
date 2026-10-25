using System.Text.Json;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>The plugin vault: a user's Microsoft sign-in in progress (its PKCE verifier).</summary>
    public sealed class PendingLinkStore(IPluginVault vault)
    {
        public sealed record PendingLink(string CodeVerifier, DateTime ExpiresAt);

        private static string LinkKey(Guid userId) => $"link:{userId}";

        public PendingLink? Get(Guid userId) =>
            vault.TryGet(LinkKey(userId), out var json) ? JsonSerializer.Deserialize<PendingLink>(json) : null;

        public void Save(Guid userId, PendingLink link) => vault.Set(LinkKey(userId), JsonSerializer.Serialize(link));

        public void Remove(Guid userId) => vault.Remove(LinkKey(userId));
    }
}
