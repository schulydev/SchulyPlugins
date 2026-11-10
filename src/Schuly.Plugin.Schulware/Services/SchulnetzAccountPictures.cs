using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Infrastructure;

namespace Schuly.Plugin.Schulware.Services
{
    /// <summary>
    /// Hands the photo from the student's own Lernendenausweis to the host as the Schulnetz account's own picture
    /// (<see cref="ILinkedAccounts.SetSchoolUserPictureAsync"/> on <see cref="SchulwareAccount.SchoolUserId"/>, never on
    /// <see cref="SchulwareAccount.LinkedAccountId"/>, which is the Microsoft account used to sign in): encrypted host storage,
    /// shown only to the owner, deleted with the account. Only the photo is kept, never the card. Refreshed at most once a day
    /// per Schulnetz account, by the sync or when the card is opened.
    /// </summary>
    public class SchulnetzAccountPictures(ILinkedAccounts linkedAccounts, ILogger<SchulnetzAccountPictures> logger)
    {
        public static readonly TimeSpan RefreshEvery = TimeSpan.FromDays(1);
        private static readonly ConcurrentDictionary<Guid, DateTimeOffset> Refreshed = new();

        public static bool Due(SchulwareAccount account) =>
            account.SchoolUserId is not null && (!Refreshed.TryGetValue(account.Id, out var at) || DateTimeOffset.UtcNow - at >= RefreshEvery);

        /// <summary>
        /// Stores the card's photo and says what happened, for the sync's summary. Never throws: a picture is decoration.
        /// A card without a photo leaves the old picture.
        /// </summary>
        public async Task<string> StoreFromCardAsync(SchulwareAccount account, string? cardHtml, CancellationToken ct)
        {
            if (account.SchoolUserId is not { } schoolUserId) return "no school profile";
            if (!Due(account)) return "not due";
            if (StudentIdCardPhoto.Extract(cardHtml) is not { } photo)
            {
                Refreshed[account.Id] = DateTimeOffset.UtcNow;
                return "no photo on the card";
            }

            try
            {
                var stored = await StoreAsync(account.ApplicationUserId, schoolUserId, photo.Bytes, photo.ContentType, ct);
                Refreshed[account.Id] = DateTimeOffset.UtcNow;
                return stored ? "stored" : "not accepted by the host";
            }
            catch (MissingMethodException)
            {
                // A host older than the picture contract: it won't learn it before a restart, which also clears this cache.
                Refreshed[account.Id] = DateTimeOffset.UtcNow;
                return "not supported by the host";
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                logger.LogInformation("Schulnetz account photo not stored ({ExceptionType})", ex.GetType().Name);
                return "not stored";
            }
        }

        // Kept apart so a host older than the picture contract fails here (MissingMethodException), caught above.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private Task<bool> StoreAsync(Guid userId, Guid schoolUserId, byte[] bytes, string contentType, CancellationToken ct) =>
            linkedAccounts.SetSchoolUserPictureAsync(userId, schoolUserId, new MemoryStream(bytes, writable: false), contentType, ct);
    }
}
