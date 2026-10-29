using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>Validation for the emoji and thumbnail routes, whose values become path or query parts upstream.</summary>
    public static partial class EmojiRequests
    {
        public static readonly string[] ThumbnailSizes = ["small", "medium", "large"];

        [GeneratedRegex("^(?:default|[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8}){0,2})$")]
        private static partial Regex LocalePattern();

        [GeneratedRegex("^[A-Za-z0-9_-]{1,120}$")]
        private static partial Regex DocumentIdPattern();

        [GeneratedRegex("^[A-Za-z0-9!=_+.-]{1,512}$")]
        private static partial Regex DriveItemIdPattern();

        public static bool IsLocale(string? locale) => locale is { Length: <= 20 } && LocalePattern().IsMatch(locale);

        public static bool IsDocumentId(string? id) => PathIds.IsSafe(DocumentIdPattern(), id);

        public static bool IsDriveItemId(string? id) => PathIds.IsSafe(DriveItemIdPattern(), id);

        public static bool IsThumbnailSize(string? size) => size is not null && ThumbnailSizes.Contains(size);
    }
}
