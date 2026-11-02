using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class SealedIdsTests
    {
        private static readonly Guid User = Guid.NewGuid();
        private static readonly Guid Account = Guid.NewGuid();
        private static readonly IdSealer Sealer = new(RandomNumberGenerator.GetBytes(32));

        public static TheoryData<string> GraphIds => new()
        {
            "19:4c1f6a0e9b2d4f0f8a8d2c4e6b1a3c5d@thread.v2",
            "48:notes",
            "AAMkAGY4OTNlNTRhLWJiMmMtNDBjZS1iOGY0LTZiNmJmNWEyOTdjZQBGAAAAAADvKtsO7NyOQ5AbVG1xS97hBwD6Q-vw2oTYSa5w7WKK-XkHAAAAAAEMAAD6Q-vw2oTYSa5w7WKK-XkHAAFLm+/Abc=",
            "1700000000123",
            "01ABCDEF2GHIJKLMNOPQRSTUVWXYZ!123",
            "0-a1b2c3d4e5f6!1-ABCDEF0123456789!42",
            "8f3c1d2e-1111-2222-3333-444455556666@1-abc!9",
            "x",
        };

        [Theory]
        [MemberData(nameof(GraphIds))]
        public void Round_trips(string id)
        {
            var sealedId = Sealer.Seal(User, Account, IdKinds.Chat, id);
            Assert.Equal(id, Sealer.Unseal(User, Account, IdKinds.Chat, sealedId));
            if (id.Length >= 8)
                Assert.DoesNotContain(id, sealedId);
            Assert.Matches("^[A-Za-z0-9_-]+$", sealedId);
            Assert.StartsWith(Sealer.KeyId, sealedId);
        }

        [Fact]
        public void Is_deterministic_per_user_account_kind_and_id()
        {
            var a = Sealer.Seal(User, Account, IdKinds.MailMessage, "AAMkAG=");
            Assert.Equal(a, Sealer.Seal(User, Account, IdKinds.MailMessage, "AAMkAG="));
            Assert.Equal(new IdSealer(SharedKey).Seal(User, Account, IdKinds.MailMessage, "AAMkAG="), new IdSealer(SharedKey).Seal(User, Account, IdKinds.MailMessage, "AAMkAG="));
            Assert.NotEqual(a, Sealer.Seal(Guid.NewGuid(), Account, IdKinds.MailMessage, "AAMkAG="));
            Assert.NotEqual(a, Sealer.Seal(User, Guid.NewGuid(), IdKinds.MailMessage, "AAMkAG="));
            Assert.NotEqual(a, Sealer.Seal(User, Account, IdKinds.MailFolder, "AAMkAG="));
            Assert.NotEqual(a, Sealer.Seal(User, Account, IdKinds.MailMessage, "AAMkAH="));
        }

        [Fact]
        public void Opens_only_for_the_same_user_account_and_kind()
        {
            var sealedId = Sealer.Seal(User, Account, IdKinds.Chat, "19:abc@thread.v2");
            Assert.Null(Sealer.Unseal(Guid.NewGuid(), Account, IdKinds.Chat, sealedId));
            Assert.Null(Sealer.Unseal(User, Guid.NewGuid(), IdKinds.Chat, sealedId));
            Assert.Null(Sealer.Unseal(User, Account, IdKinds.ChatMessage, sealedId));
            Assert.Null(Sealer.Unseal(User, Account, IdKinds.Opaque, sealedId));
        }

        [Fact]
        public void Refuses_every_single_character_change()
        {
            var sealedId = Sealer.Seal(User, Account, IdKinds.Chat, "19:abc@thread.v2");
            const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";
            for (var i = 0; i < sealedId.Length; i++)
            {
                foreach (var c in alphabet.Where(c => c != sealedId[i]).Take(6))
                {
                    var tampered = sealedId[..i] + c + sealedId[(i + 1)..];
                    Assert.Null(Sealer.Unseal(User, Account, IdKinds.Chat, tampered));
                }
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("abcd")]
        [InlineData("19:abc@thread.v2")]
        [InlineData("../../me")]
        [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
        public void Refuses_garbage(string? value) => Assert.Null(Sealer.Unseal(User, Account, IdKinds.Chat, value));

        [Fact]
        public void Refuses_truncated_and_extended_ids()
        {
            var sealedId = Sealer.Seal(User, Account, IdKinds.Chat, "19:abc@thread.v2");
            Assert.Null(Sealer.Unseal(User, Account, IdKinds.Chat, sealedId[..^1]));
            Assert.Null(Sealer.Unseal(User, Account, IdKinds.Chat, sealedId + "A"));
            Assert.Null(Sealer.Unseal(User, Account, IdKinds.Chat, sealedId + "="));
        }

        [Fact]
        public void Ids_sealed_under_an_old_key_fail_after_a_rotation()
        {
            var old = new IdSealer(RandomNumberGenerator.GetBytes(32));
            var rotated = new IdSealer(RandomNumberGenerator.GetBytes(32));
            var sealedId = old.Seal(User, Account, IdKinds.Chat, "19:abc@thread.v2");
            Assert.NotEqual(old.KeyId, rotated.KeyId);
            Assert.Null(rotated.Unseal(User, Account, IdKinds.Chat, sealedId));
            // Even with the new key id in front, the old ciphertext does not open.
            Assert.Null(rotated.Unseal(User, Account, IdKinds.Chat, rotated.KeyId + sealedId[4..]));
        }

        [Fact]
        public void Keeps_its_key_in_the_plugin_vault()
        {
            var vault = new FakeVault();
            var config = new ConfigurationBuilder().Build();
            var first = new IdSealer(vault, config).Seal(User, Account, IdKinds.Chat, "19:abc@thread.v2");
            Assert.True(vault.Contains(IdSealer.VaultKey));
            Assert.Equal("19:abc@thread.v2", new IdSealer(vault, config).Unseal(User, Account, IdKinds.Chat, first));

            vault.Remove(IdSealer.VaultKey); // rotation
            Assert.Null(new IdSealer(vault, config).Unseal(User, Account, IdKinds.Chat, first));
        }

        [Fact]
        public void A_configured_key_wins_over_the_vault()
        {
            var key = RandomNumberGenerator.GetBytes(32);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [IdSealer.ConfigKey] = Convert.ToBase64String(key) }).Build();
            var vault = new FakeVault();
            var sealedId = new IdSealer(vault, config).Seal(User, Account, IdKinds.Chat, "c");
            Assert.False(vault.Contains(IdSealer.VaultKey));
            Assert.Equal("c", new IdSealer(key).Unseal(User, Account, IdKinds.Chat, sealedId));
        }

        private static readonly byte[] SharedKey = RandomNumberGenerator.GetBytes(32);
    }
}
