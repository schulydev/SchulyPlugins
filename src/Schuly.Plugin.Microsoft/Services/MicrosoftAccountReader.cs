using System.Collections.Concurrent;
using System.Text.Json;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>Reads Microsoft 365 data for one linked account, one read per account at a time so each read uses and stores the latest rotated refresh token.</summary>
    public sealed class MicrosoftAccountReader(ILinkedAccounts linkedAccounts, MicrosoftServiceClient service)
    {
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

        public async Task<JsonElement> ReadAsync(Guid userId, Guid accountId, string path, CancellationToken ct)
        {
            var gate = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var token = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct) ?? throw new MicrosoftSignInExpiredException();
                var read = await service.ReadAsync(path, token, ct);
                if (read.RefreshToken != token)
                    await linkedAccounts.SetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, read.RefreshToken, ct);
                return read.Data;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
