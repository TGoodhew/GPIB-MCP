// -----------------------------------------------------------------------------
// Size-independent typography metrics for the StrokeFont (issue #31).
//
// These assert the font's grid-level invariants (cap height, baseline, x-height,
// descender depth, pitch) - properties that hold at every SI/SR size and on every
// instrument, NOT keyed to any one capture. They lock in the KE5FX-adopted metrics
// so a future regeneration or tweak that breaks consistency fails loudly.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class FontMetricsTests
    {
        private static IEnumerable<int> Ys(char c)
        {
            foreach (var stroke in StrokeFont.Get(c))
                for (int i = 0; i + 1 < stroke.Length; i += 2)
                    yield return stroke[i + 1];
        }

        private static IEnumerable<int> Xs(char c)
        {
            foreach (var stroke in StrokeFont.Get(c))
                for (int i = 0; i + 1 < stroke.Length; i += 2)
                    yield return stroke[i];
        }

        [Fact]
        public void Pitch_IsInstrumentDerived_1_375x_CharWidth()
        {
            Assert.Equal(1.375, StrokeFont.Advance / StrokeFont.Em, 3);
            Assert.Equal(6, StrokeFont.Cap);
        }

        [Fact]
        public void Uppercase_And_Digits_ReachCap()
        {
            foreach (char c in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")
                Assert.Equal(StrokeFont.Cap, Ys(c).Max());
        }

        [Fact]
        public void Uppercase_And_Digits_SitOnBaseline()
        {
            // Every cap/digit sits on the baseline (y=0) except 'Q', whose tail descends.
            foreach (char c in "ABCDEFGHIJKLMNOPRSTUVWXYZ0123456789")
                Assert.Equal(0, Ys(c).Min());
            Assert.True(Ys('Q').Min() < 0, "'Q' tail should dip below the baseline");
        }

        [Fact]
        public void Ascender_Lowercase_ReachCap_OnBaseline()
        {
            foreach (char c in "bdhklft")
            {
                Assert.Equal(0, Ys(c).Min());
                Assert.Equal(StrokeFont.Cap, Ys(c).Max());
            }
        }

        [Fact]
        public void XHeight_Lowercase_AreConsistent_BelowCap()
        {
            // Every x-height lowercase shares one x-height top, on the baseline, below the cap.
            var tops = "acemnorsuvwxz".Select(c => Ys(c).Max()).Distinct().ToList();
            Assert.Single(tops);                              // identical x-height across the set
            Assert.True(tops[0] < StrokeFont.Cap, "x-height must be below cap height");
            foreach (char c in "acemnorsuvwxz")
                Assert.Equal(0, Ys(c).Min());                 // all sit on the baseline
        }

        [Fact]
        public void Descenders_DipBelowBaseline()
        {
            foreach (char c in "gpqy")
                Assert.True(Ys(c).Min() < 0, "'" + c + "' should descend below the baseline");
        }

        [Fact]
        public void EveryPrintableAscii_HasAGlyph_WithinCell()
        {
            for (int code = 0x20; code <= 0x7E; code++)
            {
                char c = (char)code;
                int[][] g = StrokeFont.Get(c);
                Assert.NotNull(g);
                if (code != 0x20)
                    Assert.True(g.Length >= 1, "0x" + code.ToString("X2") + " should have strokes");
                foreach (int x in Xs(c)) Assert.InRange(x, 0, 7);   // ink stays within the cell
            }
        }

        [Fact]
        public void FullAsciiCatalog_RendersEveryGlyph()
        {
            string hpgl = BuildCatalog();
            byte[] png = HpglRenderer.RenderToPng(hpgl,
                new HpglRenderOptions { Width = 1000, Height = 520, Background = HpglBackground.White });
            using (var ms = new MemoryStream(png))
            using (var bmp = new Bitmap(ms))
            {
                int ink = 0;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.White.ToArgb()) ink++;
                Assert.True(ink > 3000, "the full ASCII catalog should draw substantial ink; got " + ink);
            }
        }

        /// <summary>HP-GL labelling all printable ASCII (0x20-0x7E) in a grid - the visual catalog (#31).</summary>
        private static string BuildCatalog()
        {
            const char etx = (char)3;
            var sb = new System.Text.StringBuilder("IN;SP1;SI0.32,0.46;");
            int y = 7200;
            for (int row = 0; row * 16 + 0x20 <= 0x7E; row++)
            {
                int lo = 0x20 + row * 16, hi = System.Math.Min(lo + 15, 0x7E);
                var chars = string.Join(" ", Enumerable.Range(lo, hi - lo + 1).Select(c => ((char)c).ToString()));
                sb.Append("PU500,").Append(y).Append(";LB").Append(chars).Append(etx).Append(';');
                y -= 1150;
            }
            return sb.Append("PU0,0;SP0;").ToString();
        }
    }
}
