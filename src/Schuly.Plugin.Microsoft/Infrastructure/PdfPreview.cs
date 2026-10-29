namespace Schuly.Plugin.Microsoft.Infrastructure
{
    /// <summary>Which files Microsoft 365 can turn into a PDF for the web viewer (Graph's <c>format=pdf</c> source types; the service checks the same list).</summary>
    public static class PdfPreview
    {
        /// <summary>A whole conversion upload: one 100 MB file plus slack (the service enforces the same cap).</summary>
        public const long MaxConvertBytes = 105L * 1024 * 1024;

        public static readonly string[] Convertible = ["csv", "doc", "docx", "odp", "ods", "odt", "pot", "potm", "potx", "pps", "ppsx", "ppsxm", "ppt", "pptm", "pptx", "rtf", "xls", "xlsx"];

        /// <summary>True for a plain file name (no path, no control characters, up to 255 characters) whose extension Graph can convert.</summary>
        public static bool IsConvertibleName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.Any(c => c < 0x20 || c == 0x7f || c == '/' || c == '\\'))
                return false;
            var dot = name.LastIndexOf('.');
            return dot > 0 && dot < name.Length - 1 && Convertible.Contains(name[(dot + 1)..].ToLowerInvariant());
        }
    }
}
