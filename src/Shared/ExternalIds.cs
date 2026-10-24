using System.Security.Cryptography;
using System.Text;

namespace Schuly.Plugin.Shared
{
    /// <summary>Builds stable external ids for synced records from the fields that identify an item.</summary>
    public static class ExternalIds
    {
        private const int MaxLength = 300;

        public static string Of(params string?[] parts)
        {
            var id = string.Join('|', parts.Select(p => p?.Trim() ?? ""));
            return id.Length <= MaxLength ? id : "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        }
    }
}
