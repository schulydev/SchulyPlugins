using System.Text.RegularExpressions;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>Pulls the student's photo out of the Schulnetz Lernendenausweis HTML, where photo, QR code and logo are inline <c>data:</c> images.</summary>
    public static partial class StudentIdCardPhoto
    {
        private const int MaxBytes = 2 * 1024 * 1024;

        // ponytail: heuristic - the photo is the largest JPEG on the card (QR and logo are PNG/SVG), else the largest raster image. Parse the card's markup if a school breaks this.
        public static (byte[] Bytes, string ContentType)? Extract(string? html)
        {
            if (string.IsNullOrEmpty(html)) return null;
            (byte[] Bytes, string ContentType)? best = null;
            var bestIsJpeg = false;
            foreach (Match m in DataImage().Matches(html))
            {
                byte[] bytes;
                try { bytes = Convert.FromBase64String(Regex.Replace(m.Groups["data"].Value, @"\s+", "")); }
                catch (FormatException) { continue; }
                if (bytes.Length == 0 || bytes.Length > MaxBytes) continue;

                var isJpeg = bytes is [0xFF, 0xD8, 0xFF, ..];
                var type = isJpeg ? "image/jpeg" : bytes is [0x89, 0x50, 0x4E, 0x47, ..] ? "image/png" : bytes.Length >= 12 && bytes[8..12].SequenceEqual("WEBP"u8.ToArray()) ? "image/webp" : null;
                if (type is null) continue;
                if (best is null || (isJpeg && !bestIsJpeg) || (isJpeg == bestIsJpeg && bytes.Length > best.Value.Bytes.Length))
                {
                    best = (bytes, type);
                    bestIsJpeg = isJpeg;
                }
            }
            return best;
        }

        [GeneratedRegex(@"data:image/(?:png|jpe?g|webp);base64,(?<data>[A-Za-z0-9+/=\s]+)", RegexOptions.IgnoreCase)]
        private static partial Regex DataImage();
    }
}
