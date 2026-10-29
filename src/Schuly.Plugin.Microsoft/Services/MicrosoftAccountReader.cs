using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>A profile photo: <see cref="Bytes"/> is null when the user has none.</summary>
    public sealed record PhotoResult(byte[]? Bytes, string ContentType);

    /// <summary>Talks to Microsoft 365 for one linked account. Upstream calls run in parallel, in two lanes per account, so interactive calls never queue behind profile photos.</summary>
    public sealed class MicrosoftAccountReader(ILinkedAccounts linkedAccounts, MicrosoftServiceClient service)
    {
        private const int CallLaneSize = 4;
        private const int PhotoLaneSize = 2;

        private sealed class AccountGates
        {
            public readonly SemaphoreSlim Calls = new(CallLaneSize, CallLaneSize);
            public readonly SemaphoreSlim Photos = new(PhotoLaneSize, PhotoLaneSize);
            public readonly SemaphoreSlim Persist = new(1, 1);
        }

        private static readonly ConcurrentDictionary<Guid, AccountGates> Gates = new();

        // Process-wide photo cache, ~1h. Keys start with the account id; callers only reach it after the controller's ownership check for that account.
        private static readonly MemoryCache Photos = new(new MemoryCacheOptions { SizeLimit = 32 * 1024 * 1024 });
        private static readonly ConcurrentDictionary<string, Lazy<Task<PhotoResult>>> PhotosInFlight = new();
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

        public Task DeleteAsync(Guid userId, Guid accountId, string path, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token => (true, await service.DeleteAsync(path, token, ct)), ct);

        /// <summary>A binary read on the normal lane, never cached (attachments are private and the service's rotated token rides on the answer).</summary>
        public Task<ServiceBinary> GetBinaryAsync(Guid userId, Guid accountId, string path, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token =>
            {
                var read = await service.GetBytesAsync(path, token, ct);
                return (read, read.RefreshToken);
            }, ct);

        public Task<PhotoResult> GetPhotoAsync(Guid userId, Guid accountId, string microsoftUserId, int size, CancellationToken ct) =>
            GetImageAsync(userId, accountId, $"user|{microsoftUserId}|{size}", $"users/{Uri.EscapeDataString(microsoftUserId)}/photo?size={size}", ct);

        /// <summary>A group chat's custom picture (null bytes when it has none).</summary>
        public Task<PhotoResult> GetChatPhotoAsync(Guid userId, Guid accountId, string chatId, CancellationToken ct) =>
            GetImageAsync(userId, accountId, $"chat|{chatId}", $"chats/{Uri.EscapeDataString(chatId)}/photo", ct);

        /// <summary>A person's photo by e-mail address (mail senders).</summary>
        public Task<PhotoResult> GetPeoplePhotoAsync(Guid userId, Guid accountId, string email, int size, CancellationToken ct) =>
            GetImageAsync(userId, accountId, $"mail|{email.ToLowerInvariant()}|{size}", $"people/photo?email={Uri.EscapeDataString(email)}&size={size}", ct);

        /// <summary>An image pasted into a chat message (a Graph hosted content).</summary>
        public Task<PhotoResult> GetChatMediaAsync(Guid userId, Guid accountId, string chatId, string messageId, string hostedId, CancellationToken ct) =>
            GetImageAsync(userId, accountId, $"media|{chatId}|{messageId}|{hostedId}", $"chats/{Uri.EscapeDataString(chatId)}/messages/{Uri.EscapeDataString(messageId)}/hostedContents/{Uri.EscapeDataString(hostedId)}", ct);

        /// <summary>Images (photos, pasted media, emoji, thumbnails) on the photo lane, cached ~1h (misses 10 min). The service answers them with no-store because the rotated token rides along; the cached copy here holds only the bytes.</summary>
        public async Task<PhotoResult> GetImageAsync(Guid userId, Guid accountId, string kind, string path, CancellationToken ct)
        {
            var key = $"{accountId:N}|{kind}";
            if (Photos.TryGetValue(key, out PhotoResult? hit) && hit is not null)
                return hit;

            // One upstream fetch per key: concurrent misses share the task. It runs detached from any single caller's token, callers just stop waiting when theirs is cancelled.
            var fetch = PhotosInFlight.GetOrAdd(key, _ => new Lazy<Task<PhotoResult>>(() => FetchImageAsync(key, userId, accountId, path)));
            return await fetch.Value.WaitAsync(ct);
        }

        private async Task<PhotoResult> FetchImageAsync(string key, Guid userId, Guid accountId, string path)
        {
            try
            {
                if (Photos.TryGetValue(key, out PhotoResult? hit) && hit is not null)
                    return hit;

                var gates = Gates.GetOrAdd(accountId, _ => new AccountGates());
                await gates.Photos.WaitAsync();
                try
                {
                    var (binary, _) = await WithTokenAsync(userId, accountId, gates, async token =>
                    {
                        var read = await service.GetBytesAsync(path, token, CancellationToken.None);
                        return (read, read.RefreshToken);
                    }, CancellationToken.None);

                    var result = new PhotoResult(binary.Bytes, binary.ContentType);
                    Photos.Set(key, result, new MemoryCacheEntryOptions { Size = Math.Max(1, binary.Bytes?.Length ?? 1), AbsoluteExpirationRelativeToNow = binary.Bytes is null ? MissTtl : PhotoTtl });
                    return result;
                }
                finally
                {
                    gates.Photos.Release();
                }
            }
            finally
            {
                PhotosInFlight.TryRemove(key, out _);
            }
        }

        /// <summary>POSTs prepared (multipart) content on the normal lane.</summary>
        public Task<JsonElement> PostContentAsync(Guid userId, Guid accountId, string path, HttpContent content, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, async token =>
            {
                var read = await service.PostContentAsync(path, content, token, ct);
                return (read.Data, read.RefreshToken);
            }, ct);

        /// <summary>Opens a streamed download; the lane is held only until the headers arrive, and the rotated token is persisted before the body is read. Null on 404.</summary>
        public Task<HttpResponseMessage?> OpenStreamAsync(Guid userId, Guid accountId, string path, CancellationToken ct) =>
            WithTokenAsync(userId, accountId, token => service.OpenStreamAsync(path, token, ct), ct);

        private async Task<T> WithTokenAsync<T>(Guid userId, Guid accountId, Func<string, Task<(T Result, string RefreshToken)>> call, CancellationToken ct)
        {
            var gates = Gates.GetOrAdd(accountId, _ => new AccountGates());
            await gates.Calls.WaitAsync(ct);
            try
            {
                var (result, _) = await WithTokenAsync(userId, accountId, gates, async token =>
                {
                    var (data, rotated) = await call(token);
                    return ((data, rotated), rotated);
                }, ct);
                return result.data;
            }
            finally
            {
                gates.Calls.Release();
            }
        }

        // Runs one upstream call with the stored refresh token and persists a rotated one. Calls overlap, so the write is a compare-and-set against the latest stored value under a short per-account lock.
        // Last writer wins: Microsoft refresh tokens stay valid until they expire (rotation issues a new one, it does not revoke the previous), so a slightly older rotated token is still good.
        private async Task<(T Result, string RefreshToken)> WithTokenAsync<T>(Guid userId, Guid accountId, AccountGates gates, Func<string, Task<(T Result, string RefreshToken)>> call, CancellationToken ct)
        {
            var token = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct) ?? throw new MicrosoftSignInExpiredException();
            var (result, rotated) = await call(token);
            if (!string.IsNullOrEmpty(rotated) && rotated != token)
            {
                await gates.Persist.WaitAsync(ct);
                try
                {
                    var latest = await linkedAccounts.GetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, ct);
                    if (latest != rotated)
                        await linkedAccounts.SetSecretAsync(userId, accountId, LinkedSecrets.RefreshToken, rotated, ct);
                }
                finally
                {
                    gates.Persist.Release();
                }
            }
            return (result, rotated);
        }
    }
}
