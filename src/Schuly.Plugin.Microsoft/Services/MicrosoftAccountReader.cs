using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>A profile photo: <see cref="Bytes"/> is null when the user has none.</summary>
    public sealed record PhotoResult(byte[]? Bytes, string ContentType);

    /// <summary>Talks to Microsoft 365 for one linked account, one upstream call per account at a time so each call uses and stores the latest rotated refresh token.</summary>
    public sealed class MicrosoftAccountReader(ILinkedAccounts linkedAccounts, MicrosoftServiceClient service)
    {
        private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

        // Process-wide photo cache, ~1h. Keys start with the account id; callers only reach it after the controller's ownership check for that account.
        private static readonly MemoryCache Photos = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
        private static readonly TimeSpan PhotoTtl = TimeSpan.FromHours(1);
        private static readonly TimeSpan MissTtl = TimeSpan.FromMinutes(10);

        public Task<JsonElement> ReadAsync(Guid userId, Guid accountId, string path, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token =>
            {
                var read = await service.ReadAsync(path, token, ct);
                return (read.Data, read.RefreshToken);
            }, ct);

        public Task<JsonElement> PostAsync(Guid userId, Guid accountId, string path, object body, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token =>
            {
                var read = await service.PostJsonAsync(path, body, token, ct);
                return (read.Data, read.RefreshToken);
            }, ct);

        public Task PostEmptyAsync(Guid userId, Guid accountId, string path, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token => (true, await service.PostEmptyAsync(path, token, ct)), ct);

        public async Task<PhotoResult> GetPhotoAsync(Guid userId, Guid accountId, string microsoftUserId, int size, CancellationToken ct)
        {
            var key = $"{accountId:N}|{microsoftUserId}|{size}";
            if (Photos.TryGetValue(key, out PhotoResult? hit) && hit is not null)
                return hit;

            var gate = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Another request for the same photo may have filled the cache while this one waited for the gate.
                if (Photos.TryGetValue(key, out hit) && hit is not null)
                    return hit;

                var token = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct) ?? throw new MicrosoftSignInExpiredException();
                var binary = await service.GetBytesAsync($"users/{Uri.EscapeDataString(microsoftUserId)}/photo?size={size}", token, ct);
                if (binary.RefreshToken != token)
                    await linkedAccounts.SetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, binary.RefreshToken, ct);

                var result = new PhotoResult(binary.Bytes, binary.ContentType);
                Photos.Set(key, result, new MemoryCacheEntryOptions { Size = Math.Max(1, binary.Bytes?.Length ?? 1), AbsoluteExpirationRelativeToNow = binary.Bytes is null ? MissTtl : PhotoTtl });
                return result;
            }
            finally
            {
                gate.Release();
            }
        }

        private async Task<T> WithTokenAsync<T>(Guid userId, Guid accountId, Func<string, Task<(T Result, string RefreshToken)>> call, CancellationToken ct)
        {
            var gate = Locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                var token = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct) ?? throw new MicrosoftSignInExpiredException();
                var (result, rotated) = await call(token);
                if (rotated != token)
                    await linkedAccounts.SetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, rotated, ct);
                return result;
            }
            finally
            {
                gate.Release();
            }
        }
    }
}
