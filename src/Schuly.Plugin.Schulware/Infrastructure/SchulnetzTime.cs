using System.Globalization;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>Converts Schulnetz wall-clock times (Europe/Zurich, no offset) to UTC.</summary>
    public static class SchulnetzTime
    {
        private static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Zurich");

        public static bool TryParseToUtc(string? raw, out DateTime utc)
        {
            utc = default;
            if (!DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var parsed))
                return false;
            if (parsed.Kind == DateTimeKind.Utc)
            {
                utc = parsed;
                return true;
            }
            return TryToUtc(parsed, out utc);
        }

        public static bool TryToUtc(DateTime wallClock, out DateTime utc)
        {
            var local = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
            if (Zone.IsInvalidTime(local))
            {
                utc = default;
                return false;
            }
            utc = TimeZoneInfo.ConvertTimeToUtc(local, Zone);
            return true;
        }
    }
}
