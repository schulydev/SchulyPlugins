using Microsoft.Extensions.Logging.Abstractions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Schuly.Plugin.Microsoft.Services;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class AccountPicturesTests
    {
        private static MicrosoftAccountPictures Pictures(Harness h) => new(h.Reader(), h.Accounts, NullLogger<MicrosoftAccountPictures>.Instance);

        private static Abstractions.LinkedAccount Account(Harness h, Guid id) => h.Accounts.Rows.Single(r => r.Account.Id == id).Account;

        private static string PhotoPath(Harness h, Guid id) => $"users/{Account(h, id).ExternalId}/photo";

        private static int PhotoCalls(Harness h, Guid id) => h.Service.Calls.Count(c => c.PathAndQuery.Contains(PhotoPath(h, id)));

        [Fact]
        public async Task The_own_photo_is_handed_to_the_host_and_refreshed_at_most_daily_unless_forced()
        {
            var h = new Harness();
            h.Service.Answers[PhotoPath(h, h.Account)] = "bytes:PNGDATA";
            var pictures = Pictures(h);

            await pictures.RefreshAsync(h.User, Account(h, h.Account), force: true, default);

            Assert.Equal("PNGDATA", System.Text.Encoding.UTF8.GetString(h.Accounts.Pictures[h.Account].Bytes!));
            Assert.Equal("image/png", h.Accounts.Pictures[h.Account].ContentType);
            Assert.Contains(h.Service.Calls, c => c.PathAndQuery.EndsWith($"{PhotoPath(h, h.Account)}?size={MicrosoftAccountPictures.PhotoSize}"));

            h.Accounts.Pictures.Clear();
            await pictures.RefreshAsync(h.User, Account(h, h.Account), force: false, default);
            Assert.Empty(h.Accounts.Pictures);

            await pictures.RefreshAsync(h.User, Account(h, h.Account), force: true, default);
            Assert.True(h.Accounts.Pictures.ContainsKey(h.Account));
        }

        [Fact]
        public async Task An_account_without_a_photo_clears_the_stored_one()
        {
            var h = new Harness();

            await Pictures(h).RefreshAsync(h.User, Account(h, h.OtherAccount), force: true, default);

            Assert.Null(h.Accounts.Pictures[h.OtherAccount].Bytes);
            Assert.Equal(1, PhotoCalls(h, h.OtherAccount));
        }

        [Fact]
        public async Task A_failing_photo_or_an_older_host_never_breaks_the_caller()
        {
            var h = new Harness();
            h.Service.Failures[PhotoPath(h, h.Account)] = (System.Net.HttpStatusCode.BadGateway, null);
            await Pictures(h).RefreshAsync(h.User, Account(h, h.Account), force: true, default);
            Assert.Empty(h.Accounts.Pictures);

            var other = new Harness();
            other.Accounts.SupportsPictures = false;
            other.Service.Answers[PhotoPath(other, other.Account)] = "bytes:PNGDATA";
            await Pictures(other).RefreshAsync(other.User, Account(other, other.Account), force: false, default);
            await Pictures(other).RefreshAsync(other.User, Account(other, other.Account), force: false, default);
            // Not stored, so not marked as refreshed: the next listing tries again (the photo lane answers it from its cache).
            Assert.Empty(other.Accounts.Pictures);
        }

        [Fact]
        public async Task Listed_accounts_point_at_their_linked_account_and_refresh_the_photo()
        {
            var h = new Harness();
            h.Service.Answers[PhotoPath(h, h.Account)] = "bytes:PNGDATA";
            var provider = new MicrosoftLinkProvider(new MicrosoftServiceClient(new FakeFactory(h.Service), h.Config), new MicrosoftAccountLinker(h.Accounts), h.Accounts, new PasswordAttemptLimiter(TimeProvider.System), h.ResponseCache, NullLogger<MicrosoftLinkProvider>.Instance, Pictures(h));

            var listed = await provider.ListAccountsAsync(h.User);

            Assert.All(listed, a => Assert.Equal(Guid.Parse(a.Id), a.LinkedAccountId));
            Assert.Null(listed[0].SchoolUserId);
            Assert.True(h.Accounts.Pictures.ContainsKey(h.Account));
        }
    }
}
