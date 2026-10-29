using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class OneNoteIdsTests
    {
        private const string Group = "3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5b";

        [Theory]
        [InlineData("1-5bb0ce61-ea45-401f-91f1-1aabe0e5a031")]
        [InlineData("0-abc!1-def")]
        [InlineData("1-{ABC}!2-d_e:f.g")]
        [InlineData(Group + "@1-abc!2-def")]
        public void Valid_ids_pass(string id) => Assert.True(OneNoteIds.IsValid(id));

        [Theory]
        [InlineData("not-a-guid@1-abc")]
        [InlineData("3f2a1b4c-5d6e-4f70-8a9b-0c1d2e3f4a5z@1-abc")]
        [InlineData("@1-abc")]
        [InlineData("1-abc@" + Group)]
        [InlineData(Group + "@1-abc@2-def")]
        [InlineData(Group + "@@1-abc")]
        public void Bad_scope_is_refused(string id) => Assert.False(OneNoteIds.IsValid(id));

        [Theory]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("1-abc..def")]
        [InlineData(Group + "@..")]
        public void Dot_segments_are_refused(string id) => Assert.False(OneNoteIds.IsValid(id));

        [Theory]
        [InlineData("")]
        [InlineData("a/b")]
        [InlineData("a\\b")]
        [InlineData("a b")]
        [InlineData("a%2Fb")]
        [InlineData("a?b")]
        [InlineData("a#b")]
        [InlineData("a=b")]
        public void Illegal_characters_are_refused(string id) => Assert.False(OneNoteIds.IsValid(id));

        [Fact]
        public void Null_is_refused() => Assert.False(OneNoteIds.IsValid(null));

        [Fact]
        public void Length_is_capped_at_300()
        {
            Assert.True(OneNoteIds.IsValid(new string('a', 300)));
            Assert.False(OneNoteIds.IsValid(new string('a', 301)));
        }
    }
}
