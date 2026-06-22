using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.RegularExpressions;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class ScreenImageTests
    {
        /// <summary>A screenshot-like image (dark bg + grid + bright traces) that compresses well, like a scope screen.</summary>
        private static Bitmap MakeScreenshot(int w, int h)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Black);
                using (var grid = new Pen(Color.FromArgb(40, 80, 40)))
                    for (int x = 0; x <= w; x += w / 10)
                    {
                        g.DrawLine(grid, x, 0, x, h);
                        g.DrawLine(grid, 0, x % h, w, x % h);
                    }
                using (var trace = new Pen(Color.Lime, 2))
                    for (int x = 0; x < w - 4; x += 4)
                        g.DrawLine(trace, x, h / 2 + (x % 60) - 30, x + 4, h / 2 + ((x + 4) % 60) - 30);
            }
            return bmp;
        }

        private static byte[] Encode(Bitmap b, ImageFormat fmt)
        {
            using (var ms = new MemoryStream()) { b.Save(ms, fmt); return ms.ToArray(); }
        }

        [Fact]
        public void ToPng_ConvertsBmp_ToPngAndShrinksIt()
        {
            using (var src = MakeScreenshot(800, 480))
            {
                byte[] bmp = Encode(src, ImageFormat.Bmp);
                byte[] png = ScreenImage.ToPng(bmp);

                // PNG magic
                Assert.True(png.Length > 8 && png[0] == 0x89 && png[1] == 0x50 && png[2] == 0x4E && png[3] == 0x47);
                // A solid-ish screenshot PNG is far smaller than the uncompressed BMP.
                Assert.True(png.Length < bmp.Length, "PNG (" + png.Length + ") should be smaller than BMP (" + bmp.Length + ")");
            }
        }

        [Fact]
        public void Dimensions_ReportsSourceSize()
        {
            using (var src = MakeScreenshot(640, 360))
            {
                ScreenImage.Dimensions(Encode(src, ImageFormat.Png), out int w, out int h);
                Assert.Equal(640, w);
                Assert.Equal(360, h);
            }
        }

        [Fact]
        public void ToBoundedInlineSvg_FitsBudget_AndEmbedsPngDataUri()
        {
            using (var src = MakeScreenshot(1024, 768))
            {
                string svg = ScreenImage.ToBoundedInlineSvg(Encode(src, ImageFormat.Png), 12000);

                Assert.NotNull(svg);
                Assert.True(svg.Length <= 12000, "inline SVG must fit the budget; was " + svg.Length);
                Assert.Contains("data:image/png;base64,", svg);
                Assert.Contains("<svg", svg);
            }
        }

        [Fact]
        public void ToBoundedInlineSvg_DownscalesLargeSource()
        {
            using (var src = MakeScreenshot(1600, 900))
            {
                string svg = ScreenImage.ToBoundedInlineSvg(Encode(src, ImageFormat.Png), 12000);
                Assert.NotNull(svg);
                int width = int.Parse(Regex.Match(svg, "width=\"(\\d+)\"").Groups[1].Value);
                Assert.True(width < 1600, "a 1600px source should be downscaled; thumbnail width = " + width);
            }
        }

        [Fact]
        public void ToBoundedInlineSvg_ProducesBlackAndWhitePreview()
        {
            // The inline preview is 1-bit B&W (so a useful-size thumbnail fits the tiny paste budget).
            // Decode the embedded PNG and confirm it only contains black/white pixels.
            using (var src = MakeScreenshot(800, 480))
            {
                string svg = ScreenImage.ToBoundedInlineSvg(Encode(src, ImageFormat.Png), 3400);
                Assert.NotNull(svg);
                string b64 = Regex.Match(svg, "base64,([^\"]+)").Groups[1].Value;
                using (var ms = new MemoryStream(System.Convert.FromBase64String(b64)))
                using (var thumb = new Bitmap(ms))
                {
                    var seen = new HashSet<int>();
                    for (int y = 0; y < thumb.Height; y += 2)
                        for (int x = 0; x < thumb.Width; x += 2)
                            seen.Add(thumb.GetPixel(x, y).ToArgb());
                    Assert.All(seen, argb =>
                    {
                        var c = Color.FromArgb(argb);
                        Assert.True((c.R == 0 && c.G == 0 && c.B == 0) || (c.R == 255 && c.G == 255 && c.B == 255),
                            "B&W preview should be pure black/white; saw " + c);
                    });
                }
            }
        }

        [Fact]
        public void ToBoundedInlineSvg_ReturnsNull_WhenBudgetTooSmall()
        {
            using (var src = MakeScreenshot(800, 480))
            {
                // No thumbnail (even the smallest) can fit a 100-char SVG -> caller falls back to file-only.
                string svg = ScreenImage.ToBoundedInlineSvg(Encode(src, ImageFormat.Png), 100);
                Assert.Null(svg);
            }
        }
    }
}
