using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Schuly.Plugin.Schulware.Infrastructure
{
    /// <summary>
    /// Cleans the Schulnetz-rendered Lernendenausweis before it leaves the backend. The card is absolutely positioned
    /// inline CSS with <c>data:</c> images (photo, QR code, logo), so those stay byte for byte; everything that could run
    /// code, navigate or load something from elsewhere goes: scripts, frames, event handlers, links and every URL that
    /// is not a <c>data:image</c>.
    /// </summary>
    public static partial class StudentIdCardHtml
    {
        /// <summary>Elements dropped together with everything inside them.</summary>
        private static readonly HashSet<string> DroppedWithContent = new(StringComparer.OrdinalIgnoreCase)
        {
            "script", "iframe", "frame", "frameset", "object", "embed", "applet", "noscript", "template", "portal", "fencedframe",
        };

        /// <summary>Elements whose tag is dropped but whose content stays (a Schulnetz page wraps everything in a form).</summary>
        private static readonly HashSet<string> DroppedTagOnly = new(StringComparer.OrdinalIgnoreCase)
        {
            "base", "link", "meta", "form", "input", "button", "select", "option", "textarea",
        };

        /// <summary>Elements whose content is raw text up to the matching end tag.</summary>
        private static readonly HashSet<string> RawText = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "textarea", "title", "xmp", "noscript", "iframe", "noembed", "noframes" };

        private static readonly HashSet<string> PlainAttributes = new(StringComparer.OrdinalIgnoreCase)
        {
            "class", "id", "style", "width", "height", "align", "valign", "border", "cellpadding", "cellspacing", "colspan", "rowspan",
            "alt", "title", "dir", "lang", "bgcolor", "color", "face", "size", "nowrap", "type", "media",
            "viewbox", "xmlns", "xmlns:xlink", "preserveaspectratio", "fill", "fill-rule", "clip-rule", "stroke", "stroke-width", "opacity",
            "d", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "points", "transform", "version", "role",
        };

        /// <summary>Attributes that may only hold a <c>data:image</c> URL.</summary>
        private static readonly HashSet<string> ImageUrlAttributes = new(StringComparer.OrdinalIgnoreCase) { "src", "href", "xlink:href" };

        private static readonly HashSet<string> LinkElements = new(StringComparer.OrdinalIgnoreCase) { "a", "area" };

        [GeneratedRegex(@"url\s*\(\s*(?<q>['""]?)(?<v>.*?)\k<q>\s*\)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
        private static partial Regex CssUrl();

        [GeneratedRegex(@"@import[^;]*;?", RegexOptions.IgnoreCase)]
        private static partial Regex CssImport();

        [GeneratedRegex(@"expression\s*\(|behavior\s*:|-moz-binding|(?:-webkit-)?image-set\s*\(|javascript\s*:", RegexOptions.IgnoreCase)]
        private static partial Regex CssActive();

        [GeneratedRegex(@"\\(?<hex>[0-9a-fA-F]{1,6})\s?|\\(?<ch>[^\r\n0-9a-fA-F])")]
        private static partial Regex CssEscape();

        [GeneratedRegex(@"[\s\x00-\x1f]+")]
        private static partial Regex Blank();

        public static string Sanitize(string? html)
        {
            if (string.IsNullOrEmpty(html)) return "";
            var output = new StringBuilder(html.Length);
            var i = 0;
            while (i < html.Length)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0)
                {
                    output.Append(html, i, html.Length - i);
                    break;
                }
                output.Append(html, i, lt - i);
                i = lt;

                if (Starts(html, i, "<!--"))
                {
                    var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                    i = end < 0 ? html.Length : end + 3;
                    continue;
                }
                if (Starts(html, i, "<!") || Starts(html, i, "<?") || Starts(html, i, "</") && !IsNameStart(At(html, i + 2)))
                {
                    var end = html.IndexOf('>', i);
                    i = end < 0 ? html.Length : end + 1;
                    continue;
                }
                if (!Starts(html, i, "</") && !IsNameStart(At(html, i + 1)))
                {
                    output.Append("&lt;");
                    i++;
                    continue;
                }

                var tag = ReadTag(html, i);
                i = tag.End;
                var name = tag.Name;

                if (DroppedWithContent.Contains(name))
                {
                    if (!tag.Closing && !tag.SelfClosing) i = SkipPast(html, i, name);
                    continue;
                }
                if (DroppedTagOnly.Contains(name)) continue;

                if (tag.Closing)
                {
                    output.Append("</").Append(name.ToLowerInvariant()).Append('>');
                    continue;
                }

                output.Append('<').Append(name.ToLowerInvariant());
                foreach (var (attrName, rawValue) in tag.Attributes)
                {
                    var value = CleanAttribute(name, attrName, rawValue);
                    if (value is null) continue;
                    output.Append(' ').Append(attrName.ToLowerInvariant());
                    if (rawValue is not null) output.Append("=\"").Append(value.Replace("\"", "&quot;")).Append('"');
                }
                output.Append(tag.SelfClosing ? " />" : ">");

                if (RawText.Contains(name) && !tag.SelfClosing)
                {
                    var close = FindClose(html, i, name);
                    var body = html[i..close];
                    output.Append(string.Equals(name, "style", StringComparison.OrdinalIgnoreCase) ? CleanCss(body).Replace("<", "") : WebUtility.HtmlEncode(WebUtility.HtmlDecode(body)));
                    i = close;
                }
            }
            return output.ToString();
        }

        /// <summary>The attribute's value to keep, or null to drop the attribute.</summary>
        private static string? CleanAttribute(string element, string name, string? raw)
        {
            if (name.StartsWith("on", StringComparison.OrdinalIgnoreCase)) return null;
            if (name.StartsWith("aria-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("data-", StringComparison.OrdinalIgnoreCase))
                return IsActiveUrl(raw) ? null : raw ?? "";
            if (ImageUrlAttributes.Contains(name))
                return !LinkElements.Contains(element) && raw is not null && IsDataImage(raw) ? raw : null;
            if (!PlainAttributes.Contains(name)) return null;
            if (string.Equals(name, "style", StringComparison.OrdinalIgnoreCase))
                return raw is null ? null : CleanCss(WebUtility.HtmlDecode(raw));
            return IsActiveUrl(raw) ? null : raw ?? "";
        }

        /// <summary>Keeps the CSS but turns every URL that is not a <c>data:image</c> into <c>none</c> and drops imports and anything that runs code.</summary>
        internal static string CleanCss(string css)
        {
            if (css.Contains('\\')) css = CssEscape().Replace(css, Unescape);
            css = CssImport().Replace(css, "");
            css = CssUrl().Replace(css, m => IsDataImage(m.Groups["v"].Value) ? m.Value : "none");
            return CssActive().Replace(css, "blocked(");
        }

        private static string Unescape(Match m)
        {
            if (m.Groups["ch"].Success) return m.Groups["ch"].Value;
            var code = Convert.ToInt32(m.Groups["hex"].Value, 16);
            return code is 0 or > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF ? "\uFFFD" : char.ConvertFromUtf32(code);
        }

        private static bool IsDataImage(string raw) =>
            Normalized(raw).StartsWith("data:image/", StringComparison.OrdinalIgnoreCase);

        /// <summary>A value that a browser could treat as a URL to run or load.</summary>
        private static bool IsActiveUrl(string? raw)
        {
            if (raw is null) return false;
            var v = Normalized(raw);
            return v.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || v.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase)
                || v.Contains("://", StringComparison.Ordinal) || v.StartsWith("//", StringComparison.Ordinal);
        }

        private static string Normalized(string raw) => Blank().Replace(WebUtility.HtmlDecode(raw), "");

        private sealed record Tag(string Name, bool Closing, bool SelfClosing, List<(string Name, string? Value)> Attributes, int End);

        private static Tag ReadTag(string html, int start)
        {
            var i = start + 1;
            var closing = At(html, i) == '/';
            if (closing) i++;
            var nameStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '/') i++;
            var name = html[nameStart..i];
            var attributes = new List<(string, string?)>();
            var selfClosing = false;

            while (i < html.Length)
            {
                while (i < html.Length && (char.IsWhiteSpace(html[i]) || html[i] == '/'))
                {
                    if (html[i] == '/' && At(html, i + 1) == '>') selfClosing = true;
                    i++;
                }
                if (i >= html.Length) break;
                if (html[i] == '>') { i++; break; }

                var attrStart = i;
                while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>' && html[i] != '=' && !(html[i] == '/' && At(html, i + 1) == '>')) i++;
                if (i == attrStart) { i++; continue; }
                var attrName = html[attrStart..i];
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                if (At(html, i) != '=')
                {
                    attributes.Add((attrName, null));
                    continue;
                }
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i])) i++;
                string value;
                if (At(html, i) is '"' or '\'')
                {
                    var quote = html[i];
                    var end = html.IndexOf(quote, i + 1);
                    if (end < 0) end = html.Length;
                    value = html[(i + 1)..end];
                    i = Math.Min(end + 1, html.Length);
                }
                else
                {
                    var valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>') i++;
                    value = html[valueStart..i];
                }
                if (IsSafeName(attrName)) attributes.Add((attrName, value));
            }

            return new Tag(name, closing, selfClosing, attributes, i);
        }

        /// <summary>Where the end tag of a dropped element ends; nested elements of the same name are counted unless the content is raw text.</summary>
        private static int SkipPast(string html, int from, string name)
        {
            if (RawText.Contains(name))
            {
                var close = FindClose(html, from, name);
                var end = html.IndexOf('>', close);
                return end < 0 ? html.Length : end + 1;
            }
            var depth = 1;
            var i = from;
            while (i < html.Length)
            {
                var lt = html.IndexOf('<', i);
                if (lt < 0) return html.Length;
                if (Starts(html, lt, "<!--"))
                {
                    var c = html.IndexOf("-->", lt + 4, StringComparison.Ordinal);
                    i = c < 0 ? html.Length : c + 3;
                    continue;
                }
                if (!IsNameStart(At(html, lt + 1)) && !(At(html, lt + 1) == '/' && IsNameStart(At(html, lt + 2))))
                {
                    i = lt + 1;
                    continue;
                }
                var tag = ReadTag(html, lt);
                i = tag.End;
                if (!string.Equals(tag.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
                if (tag.Closing) depth--;
                else if (!tag.SelfClosing) depth++;
                if (depth == 0) return i;
            }
            return html.Length;
        }

        /// <summary>Index of the <c>&lt;/name</c> that ends a raw-text element, or the end of the input.</summary>
        private static int FindClose(string html, int from, string name)
        {
            var i = from;
            while (true)
            {
                var close = html.IndexOf("</", i, StringComparison.Ordinal);
                if (close < 0) return html.Length;
                var after = close + 2 + name.Length;
                if (string.Compare(html, close + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0
                    && (after >= html.Length || char.IsWhiteSpace(html[after]) || html[after] is '>' or '/'))
                    return close;
                i = close + 2;
            }
        }

        private static bool IsSafeName(string name) => name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or ':' or '_');

        private static bool IsNameStart(char c) => char.IsAsciiLetter(c);

        private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

        private static bool Starts(string s, int i, string prefix) => string.CompareOrdinal(s, i, prefix, 0, prefix.Length) == 0;
    }
}
