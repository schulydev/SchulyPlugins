using System.Text.Json;
using Schuly.Infrastructure.Vault;
using Schuly.Plugin.OdaOrg.Data;

namespace Schuly.Plugin.OdaOrg.Services
{
    public sealed class OdaOrgSecretStore(IPluginVault vault)
    {
        private static string Key(Guid id) => $"account:{id}";

        private sealed record Credentials(string? Username, string? Password);

        public void Save(OdaOrgAccount a) =>
            vault.Set(Key(a.Id), JsonSerializer.Serialize(new Credentials(a.Username, a.Password)));

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
