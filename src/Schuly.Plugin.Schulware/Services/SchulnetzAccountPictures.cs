using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Hands the photo from the student's own Lernendenausweis to the host as the Schulnetz account's picture
    /// (<see cref="ILinkedAccounts.SetPictureAsync"/>): encrypted host storage, shown only to the owner, deleted with the account.
    /// Only the photo is kept, never the card. Refreshed at most once a day by the sync, and whenever the card is opened.
    /// </summary>
    public class SchulnetzAccountPictures(ILinkedAccounts linkedAccounts, ILogger<SchulnetzAccountPictures> logger)
    {
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);
        private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Refreshed = new();

        public static bool Due(SchulwareAccount account) =>
            account.LinkedAccountId is { } id && (!Refreshed.TryGetValue(id, out var at) || DateTimeOffset.UtcNow - at >= RefreshEvery);

        /// <summary>Stores the card's photo. Never throws: a picture is decoration. A card without a photo leaves the old picture.</summary>
        public async Task StoreFromCardAsync(SchulwareAccount account, string? cardHtml, CancellationToken ct)
        {
            if (account.LinkedAccountId is not { } linkedAccountId || StudentIdCardPhoto.Extract(cardHtml) is not { } photo) return;
            try
            {
                if (await StoreAsync(account.ApplicationUserId, linkedAccountId, photo.Bytes, photo.ContentType, ct))
                    Refreshed[linkedAccountId] = DateTimeOffset.UtcNow;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation("Schulnetz account photo not stored ({ExceptionType})", ex.GetType().Name);
            }
        }

        // Kept apart so a host older than the picture contract fails here (MissingMethodException), caught above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Task<bool> StoreAsync(Guid userId, Guid accountId, byte[] bytes, string contentType, CancellationToken ct) =>
            linkedAccounts.SetPictureAsync(userId, accountId, new MemoryStream(bytes, writable: false), contentType, ct);
    }
}
