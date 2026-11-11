using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class SchoolEventsTests
    {
        [Fact]
        public void Timed_school_events_are_kept_next_to_all_day_ones()
        {
            var records = VacationsSyncService.FromMobile([
                new EventDto { EventType = "Event", StartDate = "2026-11-27 18:15", EndDate = "2026-11-27 23:00", Text = "Volleynight BBB", RoomToken = "Sporthallen" },
                new EventDto { EventType = "Event", StartDate = "2026-11-16", EndDate = "2026-11-20", Text = "Kulturwoche (Theater)" },
                new EventDto { EventType = "Event", StartDate = "2026-12-21", EndDate = "2027-01-03", Text = "Weihnachtsferien 2026" },
                new EventDto { EventType = "Event", StartDate = "2026-11-12 08:00", Text = "Mathe", CourseToken = "M-BM23d" },
                new EventDto { EventType = "Timetable", StartDate = "2026-11-12 08:00", Text = "Lektion" },
            ]);

            Assert.Equal(3, records.Count);
            var volley = records.Single(r => r.Title == "Volleynight BBB");
            Assert.Equal((AgendaKind.Event, "Sporthallen", 17), (volley.Kind, volley.Place, volley.Start.UtcDateTime.Hour));
            Assert.NotNull(volley.End);
            Assert.Equal(AgendaKind.Event, records.Single(r => r.Title.StartsWith("Kulturwoche")).Kind);
            Assert.Equal(AgendaKind.Holiday, records.Single(r => r.Title.StartsWith("Weihnachtsferien")).Kind);
        }
    }
}
