// -----------------------------------------------------------------------------
// Render-regression test (issue #21) for Hpgl.Rendering.
//
// Renders a real HP 8563E capture (Test/test.plt) with the library's default
// options and compares it to the committed golden image (Test/test-expected.png).
// A structural break - e.g. the top-annotation clipping bug fixed in 865a5ef -
// makes this fail; cross-machine font antialiasing stays within tolerance.
//
// The HP-GL plotter-emulation technique is derived from the HP7470A Plotter
// Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class RenderRegressionTests
    {
        // Reference options the golden was generated with: library defaults
        // (1024x768, black background, antialias on) => RenderToPng(bytes).
        private const int ExpectedWidth = 1024;
        private const int ExpectedHeight = 768;

        // A pixel "differs" if any channel is off by more than this.
        private const int ChannelDelta = 32;
        // Allowed fraction of differing pixels (absorbs GDI+/font AA across machines;
        // a clipped or garbled render changes far more than this).
        private const double MaxDiffFraction = 0.02;

        private static string FixturePath(string name) =>
            Path.Combine(AppContext.BaseDirectory, "fixtures", name);

        private static Bitmap RenderTestPlot()
        {
            byte[] hpgl = File.ReadAllBytes(FixturePath("test.plt"));
            byte[] png = HpglRenderer.RenderToPng(hpgl); // default options == reference options
            using (var ms = new MemoryStream(png))
                return new Bitmap(ms);
        }

        [Fact]
        public void Render_TestPlot_MatchesGoldenWithinTolerance()
        {
            using (var rendered = RenderTestPlot())
            using (var golden = new Bitmap(FixturePath("test-expected.png")))
            {
                Assert.Equal(golden.Width, rendered.Width);
                Assert.Equal(golden.Height, rendered.Height);

                long differing = 0;
                long total = (long)rendered.Width * rendered.Height;
                for (int y = 0; y < rendered.Height; y++)
                    for (int x = 0; x < rendered.Width; x++)
                    {
                        Color a = rendered.GetPixel(x, y);
                        Color b = golden.GetPixel(x, y);
                        if (Math.Abs(a.R - b.R) > ChannelDelta ||
                            Math.Abs(a.G - b.G) > ChannelDelta ||
                            Math.Abs(a.B - b.B) > ChannelDelta)
                            differing++;
                    }

                double fraction = (double)differing / total;
                Assert.True(fraction < MaxDiffFraction,
                    $"render differs from golden in {fraction:P3} of pixels " +
                    $"(allowed < {MaxDiffFraction:P1}); {differing}/{total} px beyond ±{ChannelDelta}/channel. " +
                    "If the renderer changed intentionally, review the diff and regenerate Test/test-expected.png.");
            }
        }

        [Fact]
        public void Render_TestPlot_ProducesNonBlankImageOfExpectedSize()
        {
            using (var rendered = RenderTestPlot())
            {
                Assert.Equal(ExpectedWidth, rendered.Width);
                Assert.Equal(ExpectedHeight, rendered.Height);

                int nonBackground = CountWhere(rendered, c => c.R != 0 || c.G != 0 || c.B != 0);
                Assert.True(nonBackground > 5000,
                    "expected a substantial plot to be drawn on the black canvas, got " + nonBackground + " px");
            }
        }

        [Fact]
        public void Render_TestPlot_KeepsTopMargin_NoClippedAnnotations()
        {
            // Regression guard for the top-label clipping bug: the first row containing any
            // drawn pixel must be below the very top edge (the auto-fit reserves a margin).
            using (var rendered = RenderTestPlot())
            {
                int firstContentRow = -1;
                for (int y = 0; y < rendered.Height && firstContentRow < 0; y++)
                    for (int x = 0; x < rendered.Width; x++)
                        if (rendered.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) { firstContentRow = y; break; }

                Assert.True(firstContentRow > 0,
                    "top edge should retain a margin (no clipped annotations); first content row = " + firstContentRow);
            }
        }

        [Fact]
        public void Render_TestPlot_ContainsExpectedPenColours()
        {
            // Belt-and-braces structural check: the spectrum plot's characteristic colour families
            // are present - white graticule/annotation, the cyan trace, and the green marker.
            // (If the pen palette is intentionally re-aligned to KE5FX, update these expectations.)
            using (var rendered = RenderTestPlot())
            {
                Assert.True(CountWhere(rendered, c => c.R > 200 && c.G > 200 && c.B > 200) > 200,
                    "expected white graticule/annotation pixels");
                Assert.True(CountWhere(rendered, c => c.R < 100 && c.G > 180 && c.B > 180) > 50,
                    "expected cyan trace pixels");
                Assert.True(CountWhere(rendered, c => c.R < 130 && c.G > 180 && c.B < 130) > 10,
                    "expected green marker pixels");
            }
        }

        [Fact]
        public void Render_FeatureExercise_ExercisesFullPipeline()
        {
            // The hand-authored feature plot exercises every supported instruction family
            // (vectors, line types, arcs/circles/wedges, edge+solid+hatch+cross fills, RR,
            // 7550A polygons with a hole, stroke-font labels with SL/DI/DR/SR/SM/CR-LF,
            // RO rotation, IW clipping). It must render to rich, non-blank output.
            byte[] hpgl = File.ReadAllBytes(FixturePath("feature-exercise.plt"));

            string svg = HpglRenderer.RenderToSvg(hpgl);
            int polylines = svg.Split('M').Length - 1;
            int polygons = svg.Split(new[] { "<polygon" }, StringSplitOptions.None).Length - 1;
            Assert.True(polylines > 50, "expected many stroke/vector subpaths; got " + polylines);
            Assert.True(polygons >= 2, "expected solid-fill polygons (RA/RR); got " + polygons);

            byte[] png = HpglRenderer.RenderToPng(hpgl);
            using (var ms = new MemoryStream(png))
            using (var bmp = new Bitmap(ms))
            {
                int drawn = CountWhere(bmp, c => c.R != 0 || c.G != 0 || c.B != 0);
                Assert.True(drawn > 5000, "expected a substantial plot; got " + drawn + " px");
            }
        }

        [Fact]
        public void RenderToSvg_TestPlot_StaysUnderSizeBudget()
        {
            // #23: keep the inline SVG small so the model re-emits it as an artifact quickly. The
            // stroke-font labels + a full 601-point trace are ~21 KB; per-pen <path> coalescing plus
            // sub-pixel trace simplification keep the same 8563E capture well under budget with no
            // visible change. Guards against a size regression (e.g. reverting the path coalescing).
            byte[] hpgl = File.ReadAllBytes(FixturePath("test.plt"));
            string svg = HpglRenderer.RenderToSvg(hpgl);
            Assert.True(svg.Length < 9000,
                "8563E capture SVG should stay compact (artifact-reproduction speed); was " + svg.Length + " bytes");
        }

        private static int CountWhere(Bitmap bmp, Func<Color, bool> predicate)
        {
            int count = 0;
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    if (predicate(bmp.GetPixel(x, y))) count++;
            return count;
        }
    }
}
