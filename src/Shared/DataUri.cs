namespace Schuly.Plugin.Shared
{
    public static class DataUri
    {
        /// <summary>Decodes a base64 <c>data:&lt;type&gt;;base64,&lt;payload&gt;</c> URI.</summary>
        public static bool TryDecode(string? uri, out byte[] bytes, out string contentType)
        {
            bytes = [];
            contentType = "application/octet-stream";
            if (string.IsNullOrEmpty(uri) || !uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return false;
            var comma = uri.IndexOf(',');
            if (comma < 0) return false;

            var meta = uri[5..comma];
            if (!meta.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) return false;
            var type = meta[..^";base64".Length];
            if (type.Length > 0) contentType = type;
            try
            {
                bytes = Convert.FromBase64String(uri[(comma + 1)..]);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }
    }
}
