using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>Validation for OneNote scoped ids: a bare Graph id (personal) or <c>{groupGuid}@{graphId}</c> (team / class notebook). Ids are opaque to clients; this only checks they are safe to put in a path.</summary>
    public static class OneNoteIds
    {
        private static readonly Regex Pattern = new("^[A-Za-z0-9!@_{}:.\\-]{1,300}$", RegexOptions.Compiled);

        public static bool IsValid(string? id)
        {
            if (!PathIds.IsSafe(Pattern, id))
                return false;
            var at = id!.IndexOf('@');
            if (at < 0)
                return true;
            // At most one '@', and the scope in front of it must be a 36-char group GUID.
            return id.IndexOf('@', at + 1) < 0 && at == 36 && Guid.TryParseExact(id[..at], "D", out _);
        }
    }
}
