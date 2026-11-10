using Schuly.Plugin.Schulware.Infrastructure;
using Xunit;

namespace Schuly.Plugins.Linking.Tests
{
    public class StudentIdCardPhotoTests
    {
        private static string Data(string type, byte[] bytes) => $"data:image/{type};base64,{Convert.ToBase64String(bytes)}";

        [Fact]
        public void PicksTheJpegPhotoOverALargerPngQrCode()
        {
            byte[] photo = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3];
            byte[] qr = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[500]];
            var html = $"<div style=\"background:url('{Data("png", qr)}')\"><img src=\"{Data("jpeg", photo)}\"><img src=\"{Data("svg+xml", [1, 2])}\"></div>";

            var result = StudentIdCardPhoto.Extract(html);

            Assert.NotNull(result);
            Assert.Equal(photo, result.Value.Bytes);
            Assert.Equal("image/jpeg", result.Value.ContentType);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("<p>no images</p>")]
        [InlineData("<img src=\"data:image/png;base64,bm90IGFuIGltYWdl\">")]
        public void NoPhotoWithoutARealImage(string? html) => Assert.Null(StudentIdCardPhoto.Extract(html));
    }
}
