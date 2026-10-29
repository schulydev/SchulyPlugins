using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace Schuly.Plugin.Microsoft.Controllers
{
    /// <summary>Hardening for attachment bytes, which are untrusted and served from the app origin: always a download, never inline, never executable.</summary>
    public static partial class AttachmentResponse
    {
        private static readonly string[] BlockedTypeParts = ["html", "svg", "xml", "javascript", "ecmascript", "multipart"];

        [GeneratedRegex(@"^[A-Za-z0-9][\w.+-]{0,126}/[A-Za-z0-9][\w.+-]{0,126}$")]
        private static partial Regex MediaType();

        [GeneratedRegex("[\x00-\x1f\x7f\\/:*?\"<>|]")]
        private static partial Regex UnsafeNameChars();

        /// <summary>The upstream type when it is a well-formed, non-active media type; otherwise application/octet-stream.</summary>
        public static string SafeContentType(string? upstream)
        {
            var type = upstream?.Split(';')[0].Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(type) || !MediaType().IsMatch(type))
                return "application/octet-stream";
            return BlockedTypeParts.Any(type.Contains) ? "application/octet-stream" : type;
        }

        /// <summary>A fresh attachment disposition (with an encoded filename*) built from the name in the upstream header; the upstream header itself is never forwarded.</summary>
        public static string SafeContentDisposition(string? upstream)
        {
            string? name = null;
            if (!string.IsNullOrEmpty(upstream) && ContentDispositionHeaderValue.TryParse(upstream, out var parsed))
                name = parsed.FileNameStar ?? parsed.FileName;
            name = UnsafeNameChars().Replace(name ?? "", "_").Trim(' ', '.', '"');
            if (name.Length > 255)
                name = name[..255];
            if (name.Length == 0)
                name = "attachment";
            return new ContentDispositionHeaderValue("attachment") { FileNameStar = name }.ToString();
        }

        /// <summary>An inline disposition for a converted PDF: the sanitized upstream name with a .pdf extension. Only for bodies already checked to be application/pdf.</summary>
        public static string SafeInlinePdfDisposition(string? upstream)
        {
            var name = ContentDispositionHeaderValue.Parse(SafeContentDisposition(upstream)).FileNameStar ?? "document";
            if (!name.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                name = (name.Contains('.') ? name[..name.LastIndexOf('.')] : name) + ".pdf";
            return new ContentDispositionHeaderValue("inline") { FileNameStar = name }.ToString();
        }

        /// <summary>Headers for a converted PDF: inline (the body is application/pdf), never stored, never sniffed, sandboxed if navigated to.</summary>
        public static void ApplyInlinePdf(global::Microsoft.AspNetCore.Http.HttpResponse response, string? upstreamDisposition)
        {
            response.Headers.CacheControl = "private, no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            response.Headers.ContentDisposition = SafeInlinePdfDisposition(upstreamDisposition);
        }

        public static void Apply(global::Microsoft.AspNetCore.Http.HttpResponse response, string? upstreamDisposition)
        {
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Content-Type-Options"] = "nosniff";
            response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
            response.Headers.ContentDisposition = SafeContentDisposition(upstreamDisposition);
        }
    }
}
