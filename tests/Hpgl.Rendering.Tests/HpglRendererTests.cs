// -----------------------------------------------------------------------------
// Tests for Hpgl.Rendering.
//
// The HP-GL plotter-emulation technique is derived from the HP7470A Plotter
// Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System.Drawing;
using System.Linq;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class HpglRendererTests
    {
        // A minimal but representative plot: a graticule border, a diagonal "trace",
        // and an annotation label - the shapes an 8560-class plot is made of.
        private static readonly string SamplePlot =
            "IN;SP1;PU0,0;PD10000,0;PD10000,7000;PD0,7000;PD0,0;" + // border
            "SP2;PU500,500;PD9500,6500;" +                          // trace
            "SP1;PU500,6700;LBCF 300 MHz" + ((char)3) + ";" +     // label (ETX-terminated)
            "PU0,0;SP0;";                                           // pen up / done

        private static int CountNonBackgroundPixels(Bitmap bmp, Color background)
        {
            int count = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                {
                    var p = bmp.GetPixel(x, y);
                    if (p.R != background.R || p.G != background.G || p.B != background.B)
                        count++;
                }
            return count;
        }

        [Fact]
        public void RenderToBitmap_HonorsRequestedSize()
        {
            using (var bmp = HpglRenderer.RenderToBitmap(SamplePlot,
                       new HpglRenderOptions { Width = 800, Height = 600 }))
            {
                Assert.Equal(800, bmp.Width);
                Assert.Equal(600, bmp.Height);
            }
        }

        [Fact]
        public void RenderToBitmap_DrawsVectorsOnBlackBackground()
        {
            var opt = new HpglRenderOptions { Width = 640, Height = 480, Background = HpglBackground.Black, Antialias = false };
            using (var bmp = HpglRenderer.RenderToBitmap(SamplePlot, opt))
            {
                int drawn = CountNonBackgroundPixels(bmp, Color.Black);
                Assert.True(drawn > 500, "expected the border/trace/label to mark many pixels, got " + drawn);
            }
        }

        [Fact]
        public void RenderToBitmap_EmptyInput_ProducesBlankCanvasWithoutThrowing()
        {
            using (var bmp = HpglRenderer.RenderToBitmap("", new HpglRenderOptions { Width = 100, Height = 80 }))
            {
                Assert.Equal(100, bmp.Width);
                Assert.Equal(0, CountNonBackgroundPixels(bmp, Color.Black));
            }
        }

        [Fact]
        public void RenderToPng_ReturnsValidPngSignature()
        {
            byte[] png = HpglRenderer.RenderToPng(SamplePlot);
            Assert.True(png.Length > 8);
            // PNG magic number: 89 50 4E 47 0D 0A 1A 0A
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
                         png.Take(8).ToArray());
        }

        [Fact]
        public void RenderToPng_FromBytes_DecodesAndRenders()
        {
            byte[] bytes = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes(SamplePlot);
            byte[] png = HpglRenderer.RenderToPng(bytes);
            Assert.Equal(0x89, png[0]);
        }
    }
}
