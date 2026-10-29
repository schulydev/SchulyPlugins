using System.Net.Http.Headers;
using Schuly.Plugin.Microsoft.Controllers;
using Schuly.Plugin.Microsoft.Infrastructure;
using Xunit;

namespace Schuly.Plugin.Microsoft.Tests
{
    public class PdfPreviewTests
    {
        [Theory]
        [InlineData("Zeugnis Q1.docx")]
        [InlineData("Budget.XLSX")]
        [InlineData("slides.pptx")]
        [InlineData("notes.odt")]
        [InlineData("letter.rtf")]
        [InlineData("old.doc")]
        public void Office_names_are_convertible(string name) => Assert.True(PdfPreview.IsConvertibleName(name));

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("report.pdf")]
        [InlineData("page.html")]
        [InlineData("image.svg")]
        [InlineData("docx")]
        [InlineData(".docx")]
        [InlineData("a.")]
        [InlineData("../x.docx")]
        [InlineData(@"dir\x.docx")]
        [InlineData("a\nb.docx")]
        public void Other_names_are_refused(string? name) => Assert.False(PdfPreview.IsConvertibleName(name));

        [Fact]
        public void Overlong_names_are_refused() => Assert.False(PdfPreview.IsConvertibleName(new string('a', 252) + ".docx"));

        [Theory]
        [InlineData("inline; filename*=UTF-8''Plan.pdf", "Plan.pdf")]
        [InlineData("attachment; filename=\"Plan.docx\"", "Plan.pdf")]
        [InlineData("inline; filename*=UTF-8''..%2F..%2Fetc%2Fpasswd", "_..pdf")]
        [InlineData(null, "attachment.pdf")]
        public void Pdf_disposition_is_inline_with_a_safe_pdf_name(string? upstream, string expected)
        {
            var value = ContentDispositionHeaderValue.Parse(AttachmentResponse.SafeInlinePdfDisposition(upstream));
            Assert.Equal("inline", value.DispositionType);
            Assert.Equal(expected, value.FileNameStar);
        }
    }
}
