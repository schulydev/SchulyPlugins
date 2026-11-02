using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class TeamsFileRequestsTests
    {
        private const string Team = "0a1b2c3d-4e5f-6789-abcd-ef0123456789";

        [Theory]
        [InlineData(Team, true)]
        [InlineData("not-a-guid", false)]
        [InlineData("0a1b2c3d-4e5f-6789-abcd-ef012345678", false)]
        [InlineData("../0a1b2c3d-4e5f-6789-abcd-ef01234", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Team_ids(string? id, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsTeamId(id));

        [Theory]
        [InlineData("19:abc123_-XYZ@thread.tacv2", true)]
        [InlineData("19:abc@thread.skype", true)]
        [InlineData("19:abc@thread.v2", false)]
        [InlineData("19:abc@thread.tacv2/../x", false)]
        [InlineData("19:ab..c@thread.tacv2", false)]
        [InlineData("20:abc@thread.tacv2", false)]
        [InlineData("19:@thread.tacv2", false)]
        [InlineData("", false)]
        public void Channel_ids(string id, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsChannelId(id));

        [Theory]
        [InlineData("b!AbC-123_xyz", true)]
        [InlineData("..", false)]
        [InlineData("b!a/b", false)]
        [InlineData("b!a%2Fb", false)]
        [InlineData("b!a.b", false)]
        public void Drive_ids(string id, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsDriveId(id));

        [Theory]
        [InlineData("01ABCDEF2345!x", true)]
        [InlineData("..", false)]
        [InlineData("a/b", false)]
        [InlineData("a%2e", false)]
        public void Item_ids(string id, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsItemId(id));

        [Theory]
        [InlineData("1730000000000", true)]
        [InlineData("abc", false)]
        [InlineData("123456789012345678901", false)]
        [InlineData("", false)]
        public void Channel_message_ids(string id, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsChannelMessageId(id));

        [Theory]
        [InlineData(null, true)]
        [InlineData("Deutsch", true)]
        [InlineData("Deutsch/Prüfungen 2026", true)]
        [InlineData("", false)]
        [InlineData("/abs", false)]
        [InlineData("a/", false)]
        [InlineData("a//b", false)]
        [InlineData("a/../b", false)]
        [InlineData("a/./b", false)]
        [InlineData("..", false)]
        [InlineData("a%2Fb", false)]
        [InlineData("a\\b", false)]
        [InlineData("a:b", false)]
        [InlineData("a#b", false)]
        [InlineData("a?b", false)]
        [InlineData("a\nb", false)]
        [InlineData("a\u0000b", false)]
        public void Relative_paths(string? path, bool ok) => Assert.Equal(ok, TeamsFileRequests.IsRelativePath(path));

        [Fact]
        public void Path_length_is_capped_at_400()
        {
            Assert.True(TeamsFileRequests.IsRelativePath(new string('a', 400)));
            Assert.False(TeamsFileRequests.IsRelativePath(new string('a', 401)));
        }

        [Fact]
        public void Clamp_limits_values()
        {
            Assert.Equal(100, TeamsFileRequests.Clamp(null, 100, 1, 200));
            Assert.Equal(200, TeamsFileRequests.Clamp(5000, 100, 1, 200));
            Assert.Equal(1, TeamsFileRequests.Clamp(-4, 100, 1, 200));
        }

        [Fact]
        public void Search_trims_the_query_and_defaults_paging_and_types()
        {
            var (search, error) = TeamsFileRequests.ValidateSearch(new SearchRequest("  Masterplan ", null, null, null, null), false);
            Assert.Null(error);
            Assert.Equal("Masterplan", search!.Q);
            Assert.Equal(0, search.From);
            Assert.Equal(25, search.Size);
            Assert.Equal(TeamsFileRequests.SearchTypes, search.Types!);
        }

        [Fact]
        public void Files_search_takes_no_types()
        {
            var (search, error) = TeamsFileRequests.ValidateSearch(new SearchRequest("x", ["mail"], 10, 50, Team), true);
            Assert.Null(error);
            Assert.Null(search!.Types);
            Assert.Equal(Team, search.TeamId);
        }

        [Fact]
        public void Search_dedupes_types()
        {
            var (search, _) = TeamsFileRequests.ValidateSearch(new SearchRequest("x", ["files", "files", "chats"], 0, 1, null), false);
            Assert.Equal(["files", "chats"], search!.Types!);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("a\nb")]
        public void Search_rejects_empty_or_control_queries(string? q) =>
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest(q, null, null, null, null), false).Error);

        [Fact]
        public void Search_query_length_is_capped_at_200()
        {
            Assert.Null(TeamsFileRequests.ValidateSearch(new SearchRequest(new string('a', 200), null, null, null, null), false).Error);
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest(new string('a', 201), null, null, null, null), false).Error);
        }

        [Theory]
        [InlineData(-1, 25)]
        [InlineData(1001, 25)]
        [InlineData(0, 0)]
        [InlineData(0, 51)]
        public void Search_rejects_out_of_range_paging(int from, int size) =>
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest("x", null, from, size, null), false).Error);

        [Fact]
        public void Search_rejects_bad_types_and_team_ids()
        {
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest("x", ["sites"], null, null, null), false).Error);
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest("x", null, null, null, ""), false).Error);
            Assert.NotNull(TeamsFileRequests.ValidateSearch(new SearchRequest("x", null, null, null, new string('a', 5000)), false).Error);
            Assert.NotNull(TeamsFileRequests.ValidateSearch(null, false).Error);
        }

        [Fact]
        public void Forwarded_body_carries_the_query_only_in_the_body()
        {
            var (search, _) = TeamsFileRequests.ValidateSearch(new SearchRequest("hello", ["files"], 5, 10, "sealed-team"), false);
            var json = System.Text.Json.JsonSerializer.Serialize(search!.ToBody(Team));
            Assert.Contains("\"q\":\"hello\"", json);
            Assert.Contains("\"types\":[\"files\"]", json);
            Assert.Contains("\"from\":5", json);
            Assert.Contains(Team, json);
            Assert.DoesNotContain("sealed-team", json);
        }
    }
}
