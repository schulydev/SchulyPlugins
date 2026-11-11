using Schuly.Plugin.Abstractions;
using Schuly.Plugin.Schulware.Services;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class HomeEventsTests
    {
        private static Dictionary<string, string> Row(params (string Key, string Value)[] cells) => cells.ToDictionary(c => c.Key, c => c.Value);

        [Fact]
        public void Home_page_holidays_and_events_become_agenda_entries()
        {
            var records = HomeEventsSyncService.FromHome([
                ("Ferienübersicht", [Row(("Bezeichnung", "Sportferien 2027"), ("Von", "08.02.2027"), ("Bis", "21.02.2027"))]),
                ("Termine", [
                    Row(("Von", "11.11.2026 16:00"), ("Bis", "11.11.2026 18:00"), ("Ort", "BBB Restaurant Martinsberg"), ("Beschreibung", "Berufliche Zukunft")),
                    Row(("Von", "27.11.2026 18:15"), ("Bis", "27.11.2026"), ("Ort", "Sporthallen"), ("Beschreibung", "Volleynight BBB")),
                    Row(("Von", "08.03.2027"), ("Bis", "12.03.2027"), ("Beschreibung", "Schneesportwoche")),
                    Row(("Von", "kein Datum"), ("Beschreibung", "Kaputt")),
                ]),
                ("Ihre letzten Noten", [Row(("Von", "01.01.2027"), ("Beschreibung", "nicht hier"))]),
            ]);

            Assert.Equal(["Sportferien 2027", "Berufliche Zukunft", "Volleynight BBB", "Schneesportwoche"], records.Select(r => r.Title));
            Assert.Equal(AgendaKind.Holiday, records[0].Kind);
            Assert.NotNull(records[0].End);
            var talk = records[1];
            Assert.Equal((AgendaKind.Event, "BBB Restaurant Martinsberg", 15, 17), (talk.Kind, talk.Place, talk.Start.UtcDateTime.Hour, talk.End!.Value.UtcDateTime.Hour));
            Assert.Null(records[2].End);
            Assert.Equal(AgendaKind.Event, records[3].Kind);
        }

        [Fact]
        public void Home_page_and_mobile_agenda_use_the_same_ids()
        {
            var home = HomeEventsSyncService.FromHome([("Termine", [Row(("Von", "27.11.2026 18:15"), ("Bis", "27.11.2026 23:00"), ("Beschreibung", "Volleynight BBB"))])]);
            var mobile = VacationsSyncService.FromMobile([new Schuly.Plugin.Schulware.Client.Models.EventDto { EventType = "Event", StartDate = "2026-11-27 18:15", EndDate = "2026-11-27 23:00", Text = "Volleynight BBB" }]);
            Assert.Equal(mobile.Single().ExternalId, home.Single().ExternalId);
        }
    }
}
