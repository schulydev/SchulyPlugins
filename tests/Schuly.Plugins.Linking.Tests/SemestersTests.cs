using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SemestersTests
    {
        [Theory]
        [InlineData(2026, 11, 11, 2026, 8, 1)]
        [InlineData(2027, 1, 15, 2026, 8, 1)]
        [InlineData(2027, 2, 1, 2027, 2, 1)]
        [InlineData(2027, 7, 31, 2027, 2, 1)]
        [InlineData(2027, 8, 1, 2027, 8, 1)]
        public void The_current_semester_starts_in_august_or_february(int y, int m, int d, int sy, int sm, int sd)
        {
            Assert.Equal(new DateOnly(sy, sm, sd), Semesters.CurrentStart(new DateOnly(y, m, d)));
        }
    }
}
