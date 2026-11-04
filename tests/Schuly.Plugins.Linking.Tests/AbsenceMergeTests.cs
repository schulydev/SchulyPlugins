using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class AbsenceMergeTests
    {
        private static readonly DateOnly Day = new(2026, 8, 19);
        private static readonly DateOnly Next = new(2026, 8, 20);

        [Fact]
        public void The_same_absence_in_two_classes_is_one_record()
        {
            var records = AbsenceMerge.ToRecords([
                new AbsenceEntry(Day, Next, "Andere Absenz") { IsExcused = true, Lessons = 4, StartTime = "08:00", EndTime = "12:00" },
                new AbsenceEntry(Day, Next, "Andere Absenz") { IsExcused = true, Lessons = 3, StartTime = "07:30", EndTime = "11:00" },
            ]);

            var record = Assert.Single(records);
            Assert.Equal("absence|2026-08-19|2026-08-20", record.ExternalId);
            Assert.Equal(7, record.Lessons);
            Assert.Equal("07:30", record.StartTime);
            Assert.Equal("12:00", record.EndTime);
        }

        [Fact]
        public void A_different_reason_on_the_same_dates_stays_its_own_absence()
        {
            var records = AbsenceMerge.ToRecords([new AbsenceEntry(Day, Day, "Krankheit"), new AbsenceEntry(Day, Day, "Arzt")]);

            Assert.Equal(["absence|2026-08-19|2026-08-19", "absence|2026-08-19|2026-08-19|Krankheit"], records.Select(r => r.ExternalId));
        }

        [Fact]
        public void One_open_class_keeps_the_absence_open()
        {
            var records = AbsenceMerge.ToRecords([
                new AbsenceEntry(Day, Day, "Arzt") { IsExcused = true, TrainerStatus = "Bestätigt" },
                new AbsenceEntry(Day, Day, "Arzt") { IsExcused = false, TrainerStatus = "Offen" },
            ]);

            Assert.False(records[0].IsExcused);
            Assert.Equal("Offen", records[0].TrainerStatus);
        }

        [Fact]
        public void A_row_without_a_time_means_no_time_is_known()
        {
            var records = AbsenceMerge.ToRecords([new AbsenceEntry(Day, Day, "Arzt") { StartTime = "08:00", EndTime = "09:00" }, new AbsenceEntry(Day, Day, "Arzt")]);

            Assert.Null(records[0].StartTime);
            Assert.Null(records[0].Lessons);
        }

        [Theory]
        [InlineData("00:00", null)]
        [InlineData("13:15:00", "13:15")]
        [InlineData("", null)]
        public void Times_are_read_only_when_they_are_clear(string raw, string? expected) => Assert.Equal(expected, AbsenceMerge.Time(raw));

        [Fact]
        public void Dates_and_lessons_are_read_only_when_they_are_clear()
        {
            Assert.Equal(new DateOnly(2026, 11, 12), AbsenceMerge.Date("12.11.2026"));
            Assert.Equal(new DateOnly(2026, 11, 12), AbsenceMerge.Date("Do, 12.11.2026"));
            Assert.Equal(new DateOnly(2026, 11, 12), AbsenceMerge.Date("2026-11-12 08:00"));
            Assert.Null(AbsenceMerge.Date("soon"));
            Assert.Equal(3, AbsenceMerge.Lessons("3"));
            Assert.Null(AbsenceMerge.Lessons("1-3"));
        }
    }
}
