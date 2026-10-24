using System.Globalization;

namespace Schuly.Plugin.Shared
{
    /// <summary>Converts Swiss wall-clock times (Europe/Zurich, no offset) to UTC instants.</summary>
    public static class SwissTime
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

        public static bool TryParseToUtc(string? raw, out DateTimeOffset utc)
        {
            utc = default;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            if (parsed.Kind == DateTimeKind.Utc)
            {
                utc = new DateTimeOffset(parsed);
                return true;
            }
            return TryToUtc(parsed, out utc);
        }

        public static bool TryToUtc(DateTime wallClock, out DateTimeOffset utc)
        {
            var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            if (Zone.IsInvalidTime(local))
            {
                utc = default;
                return false;
            }
            utc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, Zone));
            return true;
        }

        /// <summary>A date-only value as the contract expects it: UTC midnight of that calendar day.</summary>
        public static DateTimeOffset UtcDate(DateOnly date) => new(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        public static DateTimeOffset UtcDate(DateTime date) => UtcDate(DateOnly.FromDateTime(date));
    }
}
