using System.Text.Json;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>The plugin vault: each linked account's refresh token, and a user's sign-in in progress.</summary>
    public sealed class MicrosoftTokenStore(IPluginVault vault)
    {
        public sealed record PendingLink(string DeviceCode, DateTime ExpiresAt);

        private static string TokenKey(Guid accountId) => $"token:{accountId}";
        private static string LinkKey(Guid userId) => $"link:{userId}";

        public string? GetToken(Guid accountId) => vault.TryGet(TokenKey(accountId), out var token) ? token : null;

        public void SaveToken(Guid accountId, string refreshToken) => vault.Set(TokenKey(accountId), refreshToken);

        public void RemoveToken(Guid accountId) => vault.Remove(TokenKey(accountId));

        public PendingLink? GetPending(Guid userId) =>
            vault.TryGet(LinkKey(userId), out var json) ? JsonSerializer.Deserialize<PendingLink>(json) : null;

        public void SavePending(Guid userId, PendingLink link) => vault.Set(LinkKey(userId), JsonSerializer.Serialize(link));

        public void RemovePending(Guid userId) => vault.Remove(LinkKey(userId));
    }
}
