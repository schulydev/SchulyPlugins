using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Services
{
    /// <summary>
    /// Hands the signed-in person's own Microsoft profile photo to the host (<see cref="ILinkedAccounts.SetPictureAsync"/>), so
    /// the accounts list shows whose account it is. The host keeps it in its encrypted storage, shows it only to the owner and
    /// deletes it with the account. Fetched when the account is linked and again at most once a day while the accounts are listed.
    /// </summary>
    public class MicrosoftAccountPictures(MicrosoftAccountReader reader, ILinkedAccounts linkedAccounts, ILogger<MicrosoftAccountPictures> logger)
    {
        public const int PhotoSize = 120;
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);
        public static readonly TimeSpan ListWait = TimeSpan.FromSeconds(3);

        private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Refreshed = new();

        /// <summary>
        /// Fetches and stores the photo, unless <paramref name="force"/> is off and it was refreshed in this process within a day.
        /// Waits at most <see cref="ListWait"/> so signing in and listing stay quick; a slower photo is cached by the photo lane and
        /// stored on the next call. Never throws: a picture is decoration.
        /// </summary>
        public async Task RefreshAsync(Guid userId, LinkedAccount account, bool force, CancellationToken ct)
        {
            if (!force && Refreshed.TryGetValue(account.Id, out var at) && DateTimeOffset.UtcNow - at < RefreshEvery)
                return;

            try
            {
                var photo = await reader.GetPhotoAsync(userId, account.Id, account.ExternalId, PhotoSize, ct).WaitAsync(ListWait, ct);
                if (await StoreAsync(userId, account.Id, photo, ct))
                    Refreshed[account.Id] = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation("Microsoft account photo not refreshed ({ExceptionType})", ex.GetType().Name);
            }
        }

        /// <summary>Forgets the refresh time of an unlinked account.</summary>
        public static void Forget(Guid accountId) => Refreshed.TryRemove(accountId, out _);

        // Kept apart so a host older than the picture contract fails here (MissingMethodException), caught by the caller, and not when the caller is compiled.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Task<bool> StoreAsync(Guid userId, Guid accountId, PhotoResult photo, CancellationToken ct) =>
            photo.Bytes is { Length: > 0 } bytes
                ? linkedAccounts.SetPictureAsync(userId, accountId, new MemoryStream(bytes, writable: false), photo.ContentType, ct)
                : linkedAccounts.SetPictureAsync(userId, accountId, null, null, ct);
    }
}
