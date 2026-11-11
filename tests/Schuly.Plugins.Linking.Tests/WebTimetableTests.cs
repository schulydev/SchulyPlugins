using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Client.Models;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class WebTimetableTests
    {
        [Fact]
        public void Lessons_and_exams_from_the_website_become_agenda_entries()
        {
            var events = new[]
            {
                new ScheduleEventDto { EventType = "0_stp", StartDate = "2026-12-09 07:30", EndDate = "2026-12-09 09:10", Kurskuerzel = "MAT-G4d-MoN", Zimmer = "7", Lehrerkuerzelname = "Muster (MuM)" },
                new ScheduleEventDto { EventType = "1_pru", StartDate = "2026-10-21 10:25:00", EndDate = "2026-10-21 11:10:00", Text = "ENG Reading" },
                new ScheduleEventDto { EventType = "2_other", StartDate = "2026-10-21 10:25", Text = "Ferien" },
                new ScheduleEventDto { EventType = "0_stp", StartDate = "not a date", Kurskuerzel = "X" },
            };
            var records = AgendaSyncService.FromWeb(events, new Dictionary<string, string> { ["MAT-G4d-MoN"] = "Mathematik" });

            Assert.Equal(2, records.Count);
            Assert.Equal((AgendaKind.Lesson, "Mathematik", "7", "Muster (MuM)"), (records[0].Kind, records[0].Title, records[0].Place, records[0].Description));
            Assert.Equal((AgendaKind.Test, "Prüfung: ENG Reading"), (records[1].Kind, records[1].Title));
        }
    }
}
