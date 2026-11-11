using System.Text;

namespace Schuly.Plugin.Demo.Services
{
    /// <summary>Writes a one-page A4 PDF of plain text lines (Helvetica, WinAnsi), enough for the demo documents.</summary>
    public static class DemoPdf
    {
        private static readonly Encoding Latin1 = Encoding.Latin1;

        public static byte[] Render(string title, IReadOnlyList<string> lines)
        {
            var content = new StringBuilder();
            content.Append("BT /F1 18 Tf 56 780 Td (").Append(Escape(title)).Append(") Tj ET\n");
            var y = 744;
            foreach (var line in lines)
            {
                content.Append("BT /F1 11 Tf 56 ").Append(y).Append(" Td (").Append(Escape(line)).Append(") Tj ET\n");
                y -= 16;
            }
            content.Append("BT /F1 8 Tf 56 40 Td (Demo-Dokument mit fiktiven Daten) Tj ET\n");
            var stream = Latin1.GetBytes(content.ToString());

            var objects = new[]
            {
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
                null,
                "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>",
            };

            using var output = new MemoryStream();
            void Write(string text) => output.Write(Latin1.GetBytes(text));
            Write("%PDF-1.4\n");
            var offsets = new long[objects.Length];
            for (var i = 0; i < objects.Length; i++)
            {
                offsets[i] = output.Position;
                Write($"{i + 1} 0 obj\n");
                if (objects[i] is { } dictionary)
                {
                    Write(dictionary);
                }
                else
                {
                    Write($"<< /Length {stream.Length} >>\nstream\n");
                    output.Write(stream);
                    Write("endstream");
                }
                Write("\nendobj\n");
            }

            var xref = output.Position;
            Write($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
            foreach (var offset in offsets)
                Write($"{offset:D10} 00000 n \n");
            Write($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }

        /// <summary>Escapes PDF string delimiters and replaces what WinAnsi can't show.</summary>
        private static string Escape(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                if (c is '\\' or '(' or ')')
                    sb.Append('\\').Append(c);
                else
                    sb.Append(c <= 'ÿ' && !char.IsControl(c) ? c : '?');
            }
            return sb.ToString();
        }
    }
}
