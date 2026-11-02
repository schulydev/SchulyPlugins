using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Abstractions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>The kinds a sealed id is bound to. A sealed id opens only as the kind it was sealed as.</summary>
    public static class IdKinds
    {
        public const string User = "user";
        public const string Chat = "chat";
        public const string ChatMessage = "chat-message";
        public const string ChatFile = "chat-file";
        public const string HostedContent = "hosted-content";
        public const string Emoji = "emoji";
        public const string MailMessage = "mail-message";
        public const string MailFolder = "mail-folder";
        public const string MailAttachment = "mail-attachment";
        public const string MailConversation = "mail-conversation";
        public const string DriveItem = "drive-item";
        public const string CalendarEvent = "calendar-event";
        public const string Notebook = "onenote-notebook";
        public const string Section = "onenote-section";
        public const string SectionGroup = "onenote-section-group";
        public const string Page = "onenote-page";
        public const string Resource = "onenote-resource";
        public const string Scheduled = "scheduled-message";
        public const string Team = "team";
        public const string Channel = "channel";
        public const string Drive = "drive";
        /// <summary>An item addressed together with its drive id (team and shared libraries): not the same kind as an own-OneDrive item.</summary>
        public const string DriveItemOther = "drive-item-other";
        public const string ChannelMessage = "channel-message";

        /// <summary>Ids no route takes (tenant, membership, iCal ids...): sealed so nothing raw leaves the plugin, but they open nowhere.</summary>
        public const string Opaque = "opaque";
    }

    /// <summary>A sealed id did not open: unknown key, wrong user, account or kind, or tampered. Controllers answer 404.</summary>
    public sealed class SealedIdException() : Exception("Unknown id.");

    /// <summary>
    /// Seals Microsoft Graph ids before they leave the plugin and opens them when they come back.
    /// <para>
    /// Format: <c>{kid}{base64url(nonce ‖ ciphertext ‖ tag)}</c>. <c>kid</c> is 4 base64url characters naming the key, so ids sealed under
    /// an older key fail cleanly (404) after a rotation. The cipher is AES-256-GCM over the UTF-8 Graph id; the associated data binds the
    /// id kind, the Schuly user id and the linked account id, so an id only opens for the same user, account and kind.
    /// </para>
    /// <para>
    /// Deterministic on purpose (SIV-style): the nonce is the first 12 bytes of HMAC-SHA256(nonceKey, associated data ‖ 0 ‖ id), so the same
    /// (user, account, kind, id) always seals to the same string and client caches, prefetch and dedupe keep working. The tradeoff: equal ids
    /// are visibly equal (to the one user who already sees both), and nothing else leaks. A nonce repeats only for the exact same input, so
    /// GCM's nonce-reuse failure (two different plaintexts under one nonce) needs an HMAC collision on 96 bits. Opening also recomputes the
    /// nonce from the decrypted id and compares it, so only ids this sealer produced are accepted.
    /// </para>
    /// <para>
    /// Keys: <c>MicrosoftApi:IdSealingKey</c> (base64, 32 bytes) when set, otherwise a random 32-byte key the plugin keeps in its own
    /// vault (encrypted at rest under the host's vault master key, HKDF info <c>schuly-vault:plugin:Microsoft 365</c>). The encryption and
    /// nonce keys are HKDF-SHA256 subkeys of it. Rotate by setting a new <c>IdSealingKey</c> or deleting the vault entry.
    /// </para>
    /// </summary>
    public sealed class IdSealer
    {
        public const string VaultKey = "sealed-ids:key";
        public const string ConfigKey = "MicrosoftApi:IdSealingKey";
        public const int MaxSealedLength = 4096;
        private const int RawMaxBytes = 2048;
        private const int KidLength = 4;
        private const int NonceSize = 12;
        private const int TagSize = 16;
        private static readonly byte[] AdPrefix = "schuly-ms-id:v1:"u8.ToArray();

        private readonly Lazy<Keys> keys;

        private sealed record Keys(string Kid, byte[] Encryption, byte[] Nonce);

        public IdSealer(IPluginVault vault, IConfiguration configuration) => keys = new Lazy<Keys>(() => Derive(LoadRoot(vault, configuration)), LazyThreadSafetyMode.ExecutionAndPublication);

        /// <summary>A sealer over a given 32-byte root key (tests, tools).</summary>
        public IdSealer(byte[] rootKey) => keys = new Lazy<Keys>(() => Derive(rootKey));

        /// <summary>The key id every sealed id starts with.</summary>
        public string KeyId => keys.Value.Kid;

        public string Seal(Guid userId, Guid accountId, string kind, string id)
        {
            ArgumentException.ThrowIfNullOrEmpty(id);
            var k = keys.Value;
            var plain = Encoding.UTF8.GetBytes(id);
            if (plain.Length > RawMaxBytes)
                throw new ArgumentException("The id is too long to seal.", nameof(id));
            var ad = AssociatedData(userId, accountId, kind);
            var nonce = Nonce(k, ad, plain);
            var output = new byte[NonceSize + plain.Length + TagSize];
            nonce.CopyTo(output, 0);
            using (var aes = new AesGcm(k.Encryption, TagSize))
                aes.Encrypt(nonce, plain, output.AsSpan(NonceSize, plain.Length), output.AsSpan(NonceSize + plain.Length, TagSize), ad);
            return k.Kid + Base64Url.EncodeToString(output);
        }

        /// <summary>The Graph id, or null when <paramref name="sealedId"/> is not one this sealer made for that user, account and kind.</summary>
        public string? Unseal(Guid userId, Guid accountId, string kind, string? sealedId)
        {
            if (sealedId is null || sealedId.Length is <= KidLength or > MaxSealedLength)
                return null;
            var k = keys.Value;
            if (!sealedId.StartsWith(k.Kid, StringComparison.Ordinal))
                return null;
            var body = sealedId.AsSpan(KidLength);
            if (!Base64Url.IsValid(body, out var length) || length <= NonceSize + TagSize)
                return null;
            var data = Base64Url.DecodeFromChars(body);
            // Only the canonical encoding is accepted, so one id has exactly one sealed spelling.
            if (!body.SequenceEqual(Base64Url.EncodeToString(data)))
                return null;
            var nonce = data.AsSpan(0, NonceSize);
            var cipher = data.AsSpan(NonceSize, data.Length - NonceSize - TagSize);
            var tag = data.AsSpan(data.Length - TagSize);
            var ad = AssociatedData(userId, accountId, kind);
            var plain = new byte[cipher.Length];
            try
            {
                using var aes = new AesGcm(k.Encryption, TagSize);
                aes.Decrypt(nonce, cipher, tag, plain, ad);
            }
            catch (CryptographicException)
            {
                return null;
            }
            if (!CryptographicOperations.FixedTimeEquals(nonce, Nonce(k, ad, plain)))
                return null;
            try
            {
                return new UTF8Encoding(false, true).GetString(plain);
            }
            catch (DecoderFallbackException)
            {
                return null;
            }
        }

        private static byte[] AssociatedData(Guid userId, Guid accountId, string kind)
        {
            ArgumentException.ThrowIfNullOrEmpty(kind);
            var kindBytes = Encoding.UTF8.GetBytes(kind);
            var ad = new byte[AdPrefix.Length + kindBytes.Length + 1 + 32];
            AdPrefix.CopyTo(ad, 0);
            kindBytes.CopyTo(ad, AdPrefix.Length);
            ad[AdPrefix.Length + kindBytes.Length] = (byte)':';
            userId.TryWriteBytes(ad.AsSpan(AdPrefix.Length + kindBytes.Length + 1, 16));
            accountId.TryWriteBytes(ad.AsSpan(AdPrefix.Length + kindBytes.Length + 17, 16));
            return ad;
        }

        private static byte[] Nonce(Keys k, byte[] ad, byte[] plain)
        {
            var input = new byte[ad.Length + 1 + plain.Length];
            ad.CopyTo(input, 0);
            plain.CopyTo(input, ad.Length + 1);
            return HMACSHA256.HashData(k.Nonce, input)[..NonceSize];
        }

        private static Keys Derive(byte[] root)
        {
            if (root is not { Length: 32 })
                throw new InvalidOperationException("The id sealing key must be 32 bytes.");
            var kid = Base64Url.EncodeToString(HMACSHA256.HashData(root, "schuly-microsoft/sealed-ids/kid"u8))[..KidLength];
            var encryption = HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: null, info: "schuly-microsoft/sealed-ids/v1/aes-gcm"u8.ToArray());
            var nonce = HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: null, info: "schuly-microsoft/sealed-ids/v1/siv-nonce"u8.ToArray());
            return new Keys(kid, encryption, nonce);
        }

        private static readonly Lock CreateLock = new();

        private static byte[] LoadRoot(IPluginVault vault, IConfiguration configuration)
        {
            if (configuration[ConfigKey] is { Length: > 0 } configured)
            {
                var bytes = Convert.FromBase64String(configured);
                return bytes.Length == 32 ? bytes : throw new InvalidOperationException($"{ConfigKey} must be 32 bytes, base64 encoded.");
            }
            lock (CreateLock)
            {
                if (vault.TryGet(VaultKey, out var stored) && Convert.TryFromBase64String(stored, new byte[32], out var n) && n == 32)
                    return Convert.FromBase64String(stored);
                var created = RandomNumberGenerator.GetBytes(32);
                vault.Set(VaultKey, Convert.ToBase64String(created));
                return created;
            }
        }
    }

    /// <summary>Sealing and opening for one request: the signed-in Schuly user and one of their linked Microsoft accounts.</summary>
    public sealed class IdScope(IdSealer sealer, Guid userId, Guid accountId)
    {
        public Guid UserId => userId;
        public Guid AccountId => accountId;

        public string Seal(string kind, string id) => sealer.Seal(userId, accountId, kind, id);

        /// <summary>The Graph id behind <paramref name="sealedId"/>; throws <see cref="SealedIdException"/> (404) when it does not open as <paramref name="kind"/> for this user and account.</summary>
        public string Open(string kind, string? sealedId) => sealer.Unseal(userId, accountId, kind, sealedId) ?? throw new SealedIdException();
    }
}
