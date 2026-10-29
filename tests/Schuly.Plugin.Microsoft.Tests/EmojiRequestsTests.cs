using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class EmojiRequestsTests
    {
        [Theory]
        [InlineData("default")]
        [InlineData("de")]
        [InlineData("de-de")]
        [InlineData("zh_CN")]
        [InlineData("sr-latn-rs")]
        public void Locales_pass(string locale) => Assert.True(EmojiRequests.IsLocale(locale));

        [Theory]
        [InlineData("")]
        [InlineData("../x")]
        [InlineData("de/de")]
        [InlineData("d")]
        [InlineData("de-de&x=1")]
        [InlineData("averyveryveryverylongvalue")]
        public void Odd_locales_are_refused(string locale) => Assert.False(EmojiRequests.IsLocale(locale));

        [Theory]
        [InlineData("0-weu-d1-abc123")]
        [InlineData("abc_DEF-9")]
        public void Document_ids_pass(string id) => Assert.True(EmojiRequests.IsDocumentId(id));

        [Theory]
        [InlineData("..")]
        [InlineData("a..b")]
        [InlineData("a/b")]
        [InlineData("a%2Fb")]
        [InlineData("")]
        public void Bad_document_ids_are_refused(string id) => Assert.False(EmojiRequests.IsDocumentId(id));

        [Theory]
        [InlineData("01ABC!x_y", true)]
        [InlineData("b!drive=1", true)]
        [InlineData("..", false)]
        [InlineData("a..b", false)]
        [InlineData(".", false)]
        [InlineData("a/b", false)]
        public void Drive_item_ids(string id, bool ok) => Assert.Equal(ok, EmojiRequests.IsDriveItemId(id));

        [Theory]
        [InlineData("small", true)]
        [InlineData("medium", true)]
        [InlineData("large", true)]
        [InlineData("huge", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Thumbnail_sizes(string? size, bool ok) => Assert.Equal(ok, EmojiRequests.IsThumbnailSize(size));
    }
}
