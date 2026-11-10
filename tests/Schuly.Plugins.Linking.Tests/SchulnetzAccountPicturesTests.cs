using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Schulware.Data;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SchulnetzAccountPicturesTests
    {
        private static readonly string Card = $"<img src=\"data:image/jpeg;base64,{Convert.ToBase64String([0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3])}\">";

        private static SchulwareAccount Account(Guid? schoolUserId) => new()
        {
            Id = Guid.NewGuid(),
            ApplicationUserId = Guid.NewGuid(),
            SchoolUserId = schoolUserId,
            LinkedAccountId = Guid.NewGuid(),
            SchulnetzBaseUrl = "https://schulnetz.example.ch",
            SchulwareApiBaseUrl = "https://api.example.ch",
        };

        private static SchulnetzAccountPictures Pictures(FakeLinkedAccounts accounts) => new(accounts, NullLogger<SchulnetzAccountPictures>.Instance);

        [Fact]
        public async Task StoresThePhotoOnTheSchoolUserNeverOnTheMicrosoftAccount()
        {
            var accounts = new FakeLinkedAccounts();
            var account = Account(Guid.NewGuid());

            Assert.True(SchulnetzAccountPictures.Due(account));
            Assert.Equal("stored", await Pictures(accounts).StoreFromCardAsync(account, Card, default));

            var stored = Assert.Single(accounts.SchoolUserPictures);
            Assert.Equal((account.ApplicationUserId, account.SchoolUserId!.Value, "image/jpeg"), stored);
            Assert.Empty(accounts.Pictures);
        }

        [Fact]
        public async Task RefreshesAtMostOncePerDayPerSchulnetzAccount()
        {
            var accounts = new FakeLinkedAccounts();
            var schoolUserId = Guid.NewGuid();
            var first = Account(schoolUserId);
            var second = Account(schoolUserId);

            await Pictures(accounts).StoreFromCardAsync(first, Card, default);

            Assert.False(SchulnetzAccountPictures.Due(first));
            Assert.Equal("not due", await Pictures(accounts).StoreFromCardAsync(first, Card, default));
            Assert.True(SchulnetzAccountPictures.Due(second));
            Assert.Single(accounts.SchoolUserPictures);
        }

        [Fact]
        public async Task SaysWhatHappenedWhenNothingWasStored()
        {
            var accounts = new FakeLinkedAccounts();

            var noProfile = Account(null);
            Assert.False(SchulnetzAccountPictures.Due(noProfile));
            Assert.Equal("no school profile", await Pictures(accounts).StoreFromCardAsync(noProfile, Card, default));

            var noPhoto = Account(Guid.NewGuid());
            Assert.Equal("no photo on the card", await Pictures(accounts).StoreFromCardAsync(noPhoto, "<p>no photo</p>", default));
            Assert.False(SchulnetzAccountPictures.Due(noPhoto));
            Assert.Empty(accounts.SchoolUserPictures);

            accounts.AcceptPictures = false;
            Assert.Equal("not accepted by the host", await Pictures(accounts).StoreFromCardAsync(Account(Guid.NewGuid()), Card, default));

            accounts.PictureError = new MissingMethodException();
            Assert.Equal("not supported by the host", await Pictures(accounts).StoreFromCardAsync(Account(Guid.NewGuid()), Card, default));
            accounts.PictureError = new HttpRequestException();
            var failed = Account(Guid.NewGuid());
            Assert.Equal("not stored", await Pictures(accounts).StoreFromCardAsync(failed, Card, default));
            Assert.True(SchulnetzAccountPictures.Due(failed));
        }
    }
}
