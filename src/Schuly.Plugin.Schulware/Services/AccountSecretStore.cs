using System.Text.Json;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.Schulware.Data;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Reads/writes a Schulware account's auth secrets to the plugin's per-plugin
    /// vault (AES-encrypted in memory), keyed by account id. The database holds only
    /// non-secret metadata; the tokens, web session and context_state live here and
    /// vanish on restart by design — that's the "vault only" guarantee.
    /// </summary>
    public sealed class AccountSecretStore(IPluginVault vault)
    {
        private static string Key(Guid id) => $"account:{id}";

        private sealed record Secrets(
            string? AccessToken,
            string? RefreshToken,
            string? WebSessionId,
            string? WebSessionUserId,
            string? WebSessionTransId,
            string? ContextStateJson,
            string? UserAgent);

        /// <summary>Encrypts and stores the account's current secret fields.</summary>
        public void Save(SchulwareAccount a) =>
            vault.Set(Key(a.Id), JsonSerializer.Serialize(new Secrets(
                a.MobileAccessToken,
                a.MobileRefreshToken,
                a.WebSessionId,
                a.WebSessionUserId,
                a.WebSessionTransId,
                a.ContextStateJson,
                a.UserAgent)));

        /// <summary>
        /// Populates the account's secret fields from the vault. Returns <c>false</c>
        /// when nothing is stored (e.g. after a restart) — the caller should then
        /// treat the account as needing reconnect.
        /// </summary>
        public bool Hydrate(SchulwareAccount a)
        {
            if (!vault.TryGet(Key(a.Id), out var json))
                return false;

            var s = JsonSerializer.Deserialize<Secrets>(json);
            if (s is null)
                return false;

            a.MobileAccessToken = s.AccessToken;
            a.MobileRefreshToken = s.RefreshToken;
            a.WebSessionId = s.WebSessionId;
            a.WebSessionUserId = s.WebSessionUserId;
            a.WebSessionTransId = s.WebSessionTransId;
            a.ContextStateJson = s.ContextStateJson;
            a.UserAgent = s.UserAgent;
            return true;
        }

        public bool Has(Guid id) => vault.Contains(Key(id));

        public void Remove(Guid id) => vault.Remove(Key(id));
    }
}
