using System.Text.RegularExpressions;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class PathIdsTests
    {
        private static readonly Regex ChatId = new("^[A-Za-z0-9:@._=-]{1,300}$");
        private static readonly Regex HostedId = new(@"^[A-Za-z0-9=_\-+/.]{1,512}$");
        private static readonly Uri Base = new("http://schulymicrosoft:8010/");

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("a/../b")]
        [InlineData("a/./b")]
        [InlineData("19:x@thread.v2..")]
        [InlineData("%2E%2E")]
        [InlineData("%252E%252E")]
        public void Dot_segments_and_encoded_dots_are_refused(string id)
        {
            Assert.False(PathIds.IsSafe(HostedId, PathIds.DecodeCatchAll(id)));
            Assert.False(PathIds.IsSafe(ChatId, id));
        }

        [Theory]
        [InlineData("19:ac92232303a94e988373cbd5bef51271@thread.v2")]
        [InlineData("48:notes")]
        public void Real_chat_ids_pass(string id) => Assert.True(PathIds.IsSafe(ChatId, id));

        [Fact]
        public void A_catch_all_is_decoded_only_once()
        {
            Assert.Equal("aWQ9/eA==", PathIds.DecodeCatchAll("aWQ9%2FeA=="));
            Assert.Equal("%2E%2E", PathIds.DecodeCatchAll("%252E%252E".Replace("%25", "%")));
            Assert.False(PathIds.IsSafe(HostedId, PathIds.DecodeCatchAll("%2E%2E")));
            Assert.True(PathIds.IsSafe(HostedId, PathIds.DecodeCatchAll("aWQ9%2FeA==")));
        }

        [Theory]
        [InlineData("chats/19%3Ax%40thread.v2/messages")]
        [InlineData("chats/48%3Anotes/photo")]
        [InlineData("people/photo?email=a%40b.ch&size=48")]
        [InlineData("chats/c/messages/1/hostedContents/aWQ9%2FeA%3D%3D")]
        public void Ordinary_paths_keep_their_target(string path) =>
            Assert.StartsWith("/api/", PathIds.Upstream(Base, path).AbsolutePath);

        [Theory]
        [InlineData("chats/../users/x/photo")]
        [InlineData("chats/%2E%2E/users")]
        [InlineData("chats/./messages")]
        [InlineData("chats/a/../../auth/device/start")]
        public void Rewritten_upstream_paths_are_refused(string path) =>
            Assert.Throws<ArgumentException>(() => PathIds.Upstream(Base, path));

        [Fact]
        public async Task The_length_limited_stream_cuts_off_past_the_cap()
        {
            await using var ok = new LengthLimitedStream(new MemoryStream(new byte[10]), 10);
            Assert.Equal(10, await ok.ReadAsync(new byte[32]));
            await using var big = new LengthLimitedStream(new MemoryStream(new byte[11]), 10);
            await Assert.ThrowsAsync<LengthLimitedStream.TooLargeException>(async () => await big.ReadAsync(new byte[32]));
        }
    }
}
