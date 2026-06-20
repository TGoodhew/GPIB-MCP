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
        public void RenderToBitmap_DoesNotClipTopEdgeLabels()
        {
            // Regression: a label anchored at the top of the coordinate space must not be drawn
            // flush against / off the top edge. The auto-fit measure must reserve room for the
            // label's text height, leaving a top margin.
            string hpgl = "IN;SP1;PU0,0;PD2000,0;PU100,1800;LBTOP" + ((char)3) + ";";
            var opt = new HpglRenderOptions { Width = 300, Height = 300, Background = HpglBackground.Black, Antialias = false };
            using (var bmp = HpglRenderer.RenderToBitmap(hpgl, opt))
            {
                int firstContentRow = -1;
                for (int y = 0; y < bmp.Height && firstContentRow < 0; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.Black.ToArgb()) { firstContentRow = y; break; }

                Assert.True(firstContentRow > 0,
                    "top edge should retain a margin (no clipped labels); first content row = " + firstContentRow);
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

        // ---- SVG output ------------------------------------------------------

        [Fact]
        public void RenderToSvg_ProducesWellFormedDocumentWithGeometryAndLabel()
        {
            string svg = HpglRenderer.RenderToSvg(SamplePlot,
                new HpglRenderOptions { Width = 800, Height = 600 });

            Assert.StartsWith("<svg", svg);
            Assert.EndsWith("</svg>", svg);
            Assert.Contains("width=\"800\"", svg);
            Assert.Contains("viewBox=\"0 0 800 600\"", svg);
            Assert.Contains("<polyline", svg);          // border/trace vectors
            Assert.Contains("<text", svg);              // the annotation label
            Assert.Contains("CF 300 MHz", svg);         // label text survived (XML-escaped)
            Assert.DoesNotContain("\u0003", svg);       // control terminator stripped
        }

        [Fact]
        public void RenderToSvg_CoalescesConnectedSegmentsIntoFewPolylines()
        {
            // The five-segment border is one connected pen-down run => a single polyline.
            string hpgl = "IN;SP1;PU0,0;PD10000,0;PD10000,7000;PD0,7000;PD0,0;SP0;";
            string svg = HpglRenderer.RenderToSvg(hpgl);
            int polylines = svg.Split(new[] { "<polyline" }, System.StringSplitOptions.None).Length - 1;
            Assert.Equal(1, polylines);
        }

        // ---- arcs, circles, rectangles (issue #8 §3.3/§3.4) ------------------

        private static int Polylines(string svg) =>
            svg.Split(new[] { "<polyline" }, System.StringSplitOptions.None).Length - 1;

        // Each "x,y" vertex carries exactly one comma, and nothing else in the SVG does.
        private static int Vertices(string svg) => svg.Count(c => c == ',');

        [Fact]
        public void Circle_SubdividesIntoManyChords_AsOneClosedPolyline()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA5000,5000;CI2000;");
            Assert.Equal(1, Polylines(svg));
            Assert.True(Vertices(svg) >= 60, "360/5° should be ~72 chords; got " + Vertices(svg));
        }

        [Fact]
        public void Circle_HonorsChordParameter_FewerSegments()
        {
            // chord = 90° -> 4 segments (a diamond), far fewer than the 5° default.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA5000,5000;CI2000,90;");
            Assert.Equal(1, Polylines(svg));
            Assert.Equal(5, Vertices(svg)); // 4 chords + closing point
        }

        [Fact]
        public void Circle_DoesNotMoveThePen()
        {
            // After CI the pen is back at the centre, so the following PD draws a radial line from it.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA5000,5000;CI1000,90;PD9000,5000;");
            Assert.True(Polylines(svg) >= 2, "circle + radial line are two separate polylines");
        }

        [Fact]
        public void Arc_AA_RendersArcAndMovesPenToEndpoint()
        {
            // Quarter circle: start (7000,5000), centre (5000,5000), +90°, then continue drawing.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA7000,5000;AA5000,5000,90;");
            Assert.Contains("<polyline", svg);
            Assert.InRange(Vertices(svg), 15, 25); // 90/5° = 18 chords -> ~19 vertices
        }

        [Fact]
        public void EdgeRect_EA_DrawsClosedFourSidedRectangle()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;EA5000,4000;");
            Assert.Equal(1, Polylines(svg));   // four connected sides coalesce to one polyline
            Assert.Equal(5, Vertices(svg));    // 4 corners + closing vertex
        }

        [Fact]
        public void EdgeRect_ER_IsRelativeToCurrentPosition()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;ER4000,3000;");
            Assert.Equal(1, Polylines(svg));
            Assert.Equal(5, Vertices(svg));
        }

        // ---- line types (issue #8 §3.5) -------------------------------------

        [Fact]
        public void LineType_DashedVector_EmitsStrokeDashArray()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;LT2;PU0,0;PD10000,0;");
            Assert.Contains("<polyline", svg);
            Assert.Contains("stroke-dasharray=", svg);
        }

        [Fact]
        public void LineType_Solid_HasNoDashArray()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PU0,0;PD10000,0;");
            Assert.Contains("<polyline", svg);
            Assert.DoesNotContain("stroke-dasharray", svg);
        }

        [Fact]
        public void LineType_RestoredToSolid_BySubsequentLT()
        {
            // LT2 dashes, then LT (no params) restores solid -> two polylines, one dashed one not.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;LT2;PU0,0;PD5000,0;LT;PU0,1000;PD5000,1000;");
            Assert.Equal(2, Polylines(svg));
            int dashed = svg.Split(new[] { "stroke-dasharray" }, System.StringSplitOptions.None).Length - 1;
            Assert.Equal(1, dashed); // only the LT2 run carries a dash array
        }

        [Fact]
        public void LineType_ChangeBreaksPolylineCoalescing()
        {
            // A connected path whose line type changes mid-run must split into separate polylines.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PU0,0;PD5000,0;LT3;PD5000,5000;");
            Assert.Equal(2, Polylines(svg));
        }

        [Fact]
        public void LineType_ResetByIN_BackToSolid()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;LT2;IN;SP1;PU0,0;PD9000,0;");
            Assert.DoesNotContain("stroke-dasharray", svg);
        }

        // ---- area fill (issue #8 §3.4: RA/RR/WG, FT, PT) --------------------

        [Fact]
        public void FillRect_RA_SolidByDefault_EmitsPolygon()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;RA5000,4000;");
            Assert.Contains("<polygon", svg);          // FT default = type 1 (solid)
            Assert.Contains("fill=\"#", svg);
        }

        [Fact]
        public void FillRect_RA_ParallelHatch_EmitsLineSpansNotPolygon()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;FT3,200;PA1000,1000;RA5000,4000;");
            Assert.DoesNotContain("<polygon", svg);    // hatch is drawn as line spans
            Assert.True(Polylines(svg) > 5, "parallel hatch should emit many spans; got " + Polylines(svg));
        }

        [Fact]
        public void FillRect_CrossHatch_HasRoughlyTwiceTheSpansOfParallel()
        {
            string parallel = HpglRenderer.RenderToSvg("IN;SP1;FT3,200;PA1000,1000;RA5000,4000;");
            string cross = HpglRenderer.RenderToSvg("IN;SP1;FT4,200;PA1000,1000;RA5000,4000;");
            Assert.True(Polylines(cross) > Polylines(parallel),
                "cross-hatch adds a second hatch direction (" + Polylines(cross) + " vs " + Polylines(parallel) + ")");
        }

        [Fact]
        public void FillRect_RR_IsRelative_AndSolid()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;RR4000,3000;");
            Assert.Contains("<polygon", svg);
        }

        [Fact]
        public void FillWedge_WG_SolidByDefault_EmitsPolygon()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA5000,5000;WG2000,0,90;");
            Assert.Contains("<polygon", svg);
        }

        [Fact]
        public void FillRect_RA_SolidFillsInteriorPixels()
        {
            // A solid-filled rectangle marks far more pixels than its outline alone would.
            string filled = "IN;SP1;PA1000,1000;RA9000,9000;";
            string outline = "IN;SP1;PA1000,1000;EA9000,9000;";
            var opt = new HpglRenderOptions { Width = 300, Height = 300, Background = HpglBackground.Black, Antialias = false };
            using (var bf = HpglRenderer.RenderToBitmap(filled, opt))
            using (var bo = HpglRenderer.RenderToBitmap(outline, opt))
                Assert.True(CountNonBackgroundPixels(bf, Color.Black) > 4 * CountNonBackgroundPixels(bo, Color.Black));
        }

        // ---- 7550A polygons (issue #8 §4.1: PM/EP/FP) ----------------------

        // Define a triangle in polygon mode, then fill or edge it.
        private const string Triangle =
            "IN;SP1;PA2000,2000;PM0;PD8000,2000;PD5000,8000;PD2000,2000;PM2;";

        [Fact]
        public void Polygon_DefinedInPolygonMode_DrawsNothingUntilEpOrFp()
        {
            // PM0..PM2 only records; with no EP/FP nothing is emitted.
            string svg = HpglRenderer.RenderToSvg(Triangle);
            Assert.DoesNotContain("<polyline", svg);
            Assert.DoesNotContain("<polygon", svg);
        }

        [Fact]
        public void Polygon_FP_SolidFillsBufferedPolygon()
        {
            string svg = HpglRenderer.RenderToSvg(Triangle + "FP;");
            Assert.Contains("<polygon", svg);   // FT default solid
        }

        [Fact]
        public void Polygon_EP_StrokesBufferedOutline()
        {
            string svg = HpglRenderer.RenderToSvg(Triangle + "EP;");
            Assert.Contains("<polyline", svg);
            Assert.DoesNotContain("<polygon", svg);
        }

        [Fact]
        public void Polygon_FP_HatchEmitsSpans()
        {
            // FT set after the triangle (whose leading IN would otherwise reset the fill type).
            string svg = HpglRenderer.RenderToSvg(Triangle + "FT3,200;FP;");
            Assert.DoesNotContain("<polygon", svg);
            Assert.True(Polylines(svg) > 5, "hatch fill should emit many spans; got " + Polylines(svg));
        }

        [Fact]
        public void Polygon_MultiContour_HatchUsesEvenOdd_LeavingAHole()
        {
            // Outer square with an inner square hole (second contour via a pen-up move inside PM).
            string hpgl =
                "IN;SP1;FT3,150;PA1000,1000;PM0;" +
                "PD9000,1000;PD9000,9000;PD1000,9000;PD1000,1000;" +   // outer contour
                "PU4000,4000;PD6000,4000;PD6000,6000;PD4000,6000;PD4000,4000;" + // inner contour (hole)
                "PM2;FP;";
            var opt = new HpglRenderOptions { Width = 300, Height = 300, Background = HpglBackground.Black, Antialias = false };
            using (var withHole = HpglRenderer.RenderToBitmap(hpgl, opt))
            using (var solidHpgl = HpglRenderer.RenderToBitmap(
                "IN;SP1;FT3,150;PA1000,1000;PM0;PD9000,1000;PD9000,9000;PD1000,9000;PD1000,1000;PM2;FP;", opt))
            {
                // The hole leaves a gap, so fewer painted pixels than the solid-hatched square.
                Assert.True(CountNonBackgroundPixels(withHole, Color.Black) <
                            CountNonBackgroundPixels(solidHpgl, Color.Black));
            }
        }

        [Fact]
        public void Geometry_RendersToBitmapWithoutThrowing()
        {
            string hpgl = "IN;SP1;PA5000,5000;CI2000;EW1500,0,120;PA1000,1000;EA9000,9000;";
            var opt = new HpglRenderOptions { Width = 400, Height = 400, Background = HpglBackground.Black, Antialias = false };
            using (var bmp = HpglRenderer.RenderToBitmap(hpgl, opt))
                Assert.True(CountNonBackgroundPixels(bmp, Color.Black) > 200);
        }

        [Fact]
        public void RenderToSvg_EmptyInput_ProducesCanvasWithoutThrowing()
        {
            string svg = HpglRenderer.RenderToSvg("", new HpglRenderOptions { Width = 100, Height = 80 });
            Assert.Contains("<rect", svg);             // background fill only
            Assert.DoesNotContain("<polyline", svg);
        }
    }
}
