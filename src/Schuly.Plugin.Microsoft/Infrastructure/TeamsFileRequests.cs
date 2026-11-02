using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>The JSON body of the search routes. Everything is optional here so the validator can answer with a message instead of a model-binding error.</summary>
    public sealed record SearchRequest(string? Q, string[]? Types, int? From, int? Size, string? TeamId);

    /// <summary>A search request that passed <see cref="TeamsFileRequests.ValidateSearch"/>: trimmed query, known types, clamped paging. <see cref="Types"/> is null for the files-only route. <see cref="TeamId"/> is still the sealed id; the controller opens it.</summary>
    public sealed record ValidSearch(string Q, string[]? Types, int From, int Size, string? TeamId)
    {
        /// <summary>The body forwarded upstream, in one place per route, with the opened (raw) team id. The query only ever travels in this body.</summary>
        public object ToBody(string? rawTeamId) => Types is null
            ? new { q = Q, from = From, size = Size, teamId = rawTeamId }
            : new { q = Q, types = Types, from = From, size = Size, teamId = rawTeamId };
    }

    /// <summary>Validation for the Teams / shared files, search, channel message and file text routes, whose values become path or query parts upstream.</summary>
    public static partial class TeamsFileRequests
    {
        public static readonly string[] SearchTypes = ["files", "mail", "chats", "channels"];
        public const int MaxQueryLength = 200;
        public const int MaxPathLength = 400;
        public const int MaxFrom = 1000;
        public const int MaxSize = 50;
        public const int DefaultSize = 25;
        public const int MaxTop = 200;
        public const int DefaultListTop = 100;
        public const int DefaultMessageTop = 50;
        public const int DefaultMaxChars = 20000;
        public const int MaxMaxChars = 100000;
        public const int DefaultMaxRows = 500;
        public const int MaxMaxRows = 2000;
        public const int MaxSheetNameLength = 100;
        public const long MaxContentBytes = 100L * 1024 * 1024;
        public const long MaxTextConvertBytes = 25L * 1024 * 1024;

        [GeneratedRegex("^[0-9a-fA-F-]{36}$")]
        private static partial Regex TeamIdPattern();

        [GeneratedRegex(@"^19:[A-Za-z0-9_-]{1,200}@thread\.(tacv2|skype)$")]
        private static partial Regex ChannelIdPattern();

        [GeneratedRegex("^[A-Za-z0-9!_-]{1,200}$")]
        private static partial Regex DriveIdPattern();

        [GeneratedRegex("^[0-9]{1,20}$")]
        private static partial Regex ChannelMessageIdPattern();

        public static bool IsTeamId(string? id) => PathIds.IsSafe(TeamIdPattern(), id) && Guid.TryParse(id, out _);

        public static bool IsChannelId(string? id) => PathIds.IsSafe(ChannelIdPattern(), id);

        public static bool IsDriveId(string? id) => PathIds.IsSafe(DriveIdPattern(), id);

        public static bool IsItemId(string? id) => EmojiRequests.IsDriveItemId(id);

        public static bool IsChannelMessageId(string? id) => PathIds.IsSafe(ChannelMessageIdPattern(), id);

        /// <summary>Null (no sub-folder) or slash separated folder names: no empty or dot segments, no <c>% \ : # ?</c>, no control characters, at most 400 characters.</summary>
        public static bool IsRelativePath(string? path)
        {
            if (path is null)
                return true;
            if (path.Length == 0 || path.Length > MaxPathLength || path.StartsWith('/') || path.EndsWith('/'))
                return false;
            if (path.Any(c => char.IsControl(c) || c is '%' or '\\' or ':' or '#' or '?'))
                return false;
            return !PathIds.HasDotSegment(path) && path.Split('/').All(segment => segment.Length > 0 && segment.Trim().Length > 0);
        }

        /// <summary>A sheet name for the spreadsheet text route: printable, at most 100 characters, none of the characters that would break out of a query value.</summary>
        public static bool IsSheetName(string? sheet) =>
            sheet is { Length: > 0 and <= MaxSheetNameLength } && !sheet.Any(c => char.IsControl(c) || c is '%' or '\\' or '#' or '?' or '&' or '=');

        public static int Clamp(int? value, int fallback, int min, int max) => Math.Clamp(value ?? fallback, min, max);

        /// <summary>The validated search, or the message to answer 400 with. <paramref name="filesOnly"/> is the files/search route, which takes no types.</summary>
        public static (ValidSearch? Search, string? Error) ValidateSearch(SearchRequest? request, bool filesOnly)
        {
            if (request is null)
                return (null, "The body is empty.");
            var q = request.Q?.Trim();
            if (string.IsNullOrEmpty(q) || q.Length > MaxQueryLength || q.Any(char.IsControl))
                return (null, "q must be 1 to 200 characters.");
            var from = request.From ?? 0;
            if (from is < 0 or > MaxFrom)
                return (null, "from must be between 0 and 1000.");
            var size = request.Size ?? DefaultSize;
            if (size is < 1 or > MaxSize)
                return (null, "size must be between 1 and 50.");
            // The team id arrives sealed: only its length is bounded here, the controller opens it and checks the raw id with IsTeamId.
            if (request.TeamId is not null && (request.TeamId.Length == 0 || request.TeamId.Length > IdSealer.MaxSealedLength))
                return (null, "Invalid team id.");
            string[]? types = null;
            if (!filesOnly)
            {
                var asked = request.Types is { Length: > 0 } ? request.Types : SearchTypes;
                if (asked.Length > SearchTypes.Length || asked.Any(t => t is null || !SearchTypes.Contains(t)))
                    return (null, "types must be a subset of files, mail, chats and channels.");
                types = asked.Distinct().ToArray();
            }
            return (new ValidSearch(q, types, from, size, request.TeamId), null);
        }
    }
}
