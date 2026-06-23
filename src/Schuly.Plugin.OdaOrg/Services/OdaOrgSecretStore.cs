using System.Text.Json;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.OdaOrg.Data;

namespace Schuly.Plugin.OdaOrg.Services
{
    /// <summary>
    /// Reads/writes an OdaOrg account's login credentials to the plugin's per-plugin
    /// vault (AES-encrypted in memory), keyed by account id. The database holds only
    /// non-secret metadata; the username/password live here and vanish on restart by
    /// design — "vault only".
    /// </summary>
    public sealed class OdaOrgSecretStore(IPluginVault vault)
    {
        private static string Key(Guid id) => $"account:{id}";

        private sealed record Credentials(string? Username, string? Password);

        public void Save(OdaOrgAccount a) =>
            vault.Set(Key(a.Id), JsonSerializer.Serialize(new Credentials(a.Username, a.Password)));

        /// <summary>Populates the account's credentials from the vault. Returns <c>false</c> when none are stored (e.g. after a restart).</summary>
        public bool Hydrate(OdaOrgAccount a)
        {
            if (!vault.TryGet(Key(a.Id), out var json))
                return false;

            var c = JsonSerializer.Deserialize<Credentials>(json);
            if (c is null)
                return false;

            a.Username = c.Username;
            a.Password = c.Password;
            return true;
        }

        public bool Has(Guid id) => vault.Contains(Key(id));

        public void Remove(Guid id) => vault.Remove(Key(id));
    }
}
