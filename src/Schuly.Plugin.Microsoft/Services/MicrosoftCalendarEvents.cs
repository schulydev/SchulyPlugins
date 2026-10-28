using System.Globalization;
using System.Text.Json;

namespace Schuly.Plugin.Microsoft.Services
{
    public sealed record MicrosoftCalendarAccount(Guid Id, string Email, string? DisplayName, string Status, string? Message);

    /// <summary>An event of a linked account. <c>Start</c>/<c>End</c> are UTC ISO timestamps, or <c>yyyy-MM-dd</c> dates for all-day events (end exclusive).</summary>
    public sealed record MicrosoftCalendarEvent(string Id, Guid AccountId, string Title, string Start, string End, bool IsAllDay, string? Location, string? Organizer, string? Preview, string? WebLink, string? JoinUrl, string? ShowAs, bool IsCancelled);

    public sealed record MicrosoftCalendarFeed(IReadOnlyList<MicrosoftCalendarAccount> Accounts, IReadOnlyList<MicrosoftCalendarEvent> Events);

    /// <summary>Maps Microsoft Graph event JSON onto <see cref="MicrosoftCalendarEvent"/> and merges several accounts' events.</summary>
    public static class MicrosoftCalendarEvents
    {
        public static List<MicrosoftCalendarEvent> Parse(Guid accountId, JsonElement events)
        {
            var result = new List<MicrosoftCalendarEvent>();
            if (events.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var item in events.EnumerateArray())
            {
                var isAllDay = Bool(item, "isAllDay");
                var start = Time(item, "start", isAllDay);
                var end = Time(item, "end", isAllDay);
                if (start is null || end is null)
                    continue;

                result.Add(new MicrosoftCalendarEvent(
                    Str(item, "iCalUId") ?? Str(item, "id") ?? Guid.NewGuid().ToString(),
                    accountId,
                    Str(item, "subject") is { Length: > 0 } subject ? subject : "(No title)",
                    start,
                    end,
                    isAllDay,
                    Nested(item, "location", "displayName"),
                    Nested(item, "organizer", "emailAddress", "name"),
                    Str(item, "bodyPreview"),
                    Str(item, "webLink"),
                    Nested(item, "onlineMeeting", "joinUrl") ?? Str(item, "onlineMeetingUrl"),
                    Str(item, "showAs"),
                    Bool(item, "isCancelled")));
            }

            return result;
        }

        public static List<MicrosoftCalendarEvent> Merge(IEnumerable<IEnumerable<MicrosoftCalendarEvent>> perAccount)
        {
            var seen = new HashSet<(string, string)>();
            return perAccount
                .SelectMany(events => events)
                .Where(e => seen.Add((e.Id, e.Start)))
                .OrderBy(e => e.Start, StringComparer.Ordinal)
                .ToList();
        }

        private static string? Time(JsonElement item, string name, bool isAllDay)
        {
            var dateTime = Nested(item, name, "dateTime");
            if (dateTime is null || !DateTime.TryParse(dateTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                return null;
            if (isAllDay)
                return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

            var zone = Nested(item, name, "timeZone");
            var utc = zone is null or "UTC" || !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out var tz)
                ? DateTime.SpecifyKind(local, DateTimeKind.Utc)
                : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
            return utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        private static string? Str(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

        private static bool Bool(JsonElement element, string name) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

        private static string? Nested(JsonElement element, params string[] path)
        {
            for (var i = 0; i < path.Length - 1; i++)
            {
                if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(path[i], out element))
                    return null;
            }
            return Str(element, path[^1]) is { Length: > 0 } value ? value : null;
        }
    }
}
