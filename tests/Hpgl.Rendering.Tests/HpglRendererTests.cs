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
            Assert.Contains("viewBox=\"0 0 800 600\"", svg);     // responsive: viewBox only, no fixed svg size
            Assert.DoesNotContain("<svg xmlns=\"http://www.w3.org/2000/svg\" width=", svg); // svg tag carries no intrinsic px size
            Assert.Contains("<path", svg);          // border/trace vectors + label strokes

            // Labels are drawn as vector strokes (a single-stroke font), not <text>, so the same
            // plot without the label produces strictly fewer polylines.
            string noLabel =
                "IN;SP1;PU0,0;PD10000,0;PD10000,7000;PD0,7000;PD0,0;" +
                "SP2;PU500,500;PD9500,6500;PU0,0;SP0;";
            string svgNoLabel = HpglRenderer.RenderToSvg(noLabel, new HpglRenderOptions { Width = 800, Height = 600 });
            Assert.True(Polylines(svg) > Polylines(svgNoLabel),
                "the annotation label should add stroke polylines (" + Polylines(svg) + " vs " + Polylines(svgNoLabel) + ")");
            Assert.DoesNotContain("\u0003", svg);       // control terminator stripped
        }

        [Fact]
        public void RenderToSvg_CoalescesConnectedSegmentsIntoFewPolylines()
        {
            // The five-segment border is one connected pen-down run => a single polyline.
            string hpgl = "IN;SP1;PU0,0;PD10000,0;PD10000,7000;PD0,7000;PD0,0;SP0;";
            string svg = HpglRenderer.RenderToSvg(hpgl);
            int polylines = svg.Count(c => c == 'M');
            Assert.Equal(1, polylines);
        }

        // ---- arcs, circles, rectangles (issue #8 §3.3/§3.4) ------------------

        private static int Polylines(string svg) =>
            svg.Count(c => c == 'M');

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
            Assert.Contains("<path", svg);
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
            Assert.Contains("<path", svg);
            Assert.Contains("stroke-dasharray=", svg);
        }

        [Fact]
        public void LineType_Solid_HasNoDashArray()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PU0,0;PD10000,0;");
            Assert.Contains("<path", svg);
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
            Assert.DoesNotContain("<path", svg);
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
            Assert.Contains("<path", svg);
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

        // ---- default P1/P2 frame aspect (issue #28) ------------------------

        private static Rectangle DrawnBBox(string hpgl)
        {
            var opt = new HpglRenderOptions { Width = 400, Height = 400, Background = HpglBackground.Black, Antialias = false };
            using (var bmp = HpglRenderer.RenderToBitmap(hpgl, opt))
            {
                int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
                for (int y = 0; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                        if (bmp.GetPixel(x, y).ToArgb() != Color.Black.ToArgb())
                        {
                            if (x < minX) minX = x; if (x > maxX) maxX = x;
                            if (y < minY) minY = y; if (y > maxY) maxY = y;
                        }
                return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
            }
        }

        [Fact]
        public void ScaleOnDefaultFrame_DrawsEllipse_NotCircle()
        {
            // The default P1/P2 frame is non-square (landscape ~10000x7200), so SC of a SQUARE user
            // range makes a circle render as a wider ellipse - as a real HP 7475A/7550A does (#28).
            var b = DrawnBBox("IN;SC0,500,0,500;PA250,250;CI100;");
            Assert.True(b.Width > b.Height * 1.25,
                "SC circle on the default frame should be a wider ellipse; w=" + b.Width + " h=" + b.Height);
        }

        [Fact]
        public void ScaleOnSquareFrame_DrawsCircle()
        {
            // With IP forcing a square P1/P2, the same SC circle stays round.
            var b = DrawnBBox("IN;IP0,0,10000,10000;SC0,500,0,500;PA250,250;CI100;");
            double ratio = (double)b.Width / b.Height;
            Assert.InRange(ratio, 0.9, 1.1);
        }

        // ---- ticks (issue #8 §3.5: TL/XT/YT) -------------------------------

        [Fact]
        public void Ticks_XT_YT_DrawTwoCrossedSegments()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;TL2,2;PA5000,5000;XT;YT;");
            Assert.Equal(2, Polylines(svg));   // one vertical + one horizontal tick
            Assert.Equal(4, Vertices(svg));    // two endpoints each
        }

        // ---- user-defined fill (issue #8 §4.2: UF) -------------------------

        [Fact]
        public void UserFill_UF_VariableSpacingHatch_StillFillsAsSpans()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;UF150,400,150;FT3;RA9000,9000;");
            Assert.DoesNotContain("<polygon", svg);            // hatch, not solid
            Assert.True(Polylines(svg) > 3, "UF hatch should still emit spans; got " + Polylines(svg));
        }

        // ---- HP-GL/2 encoded polyline (issue #8 §12: PE) -------------------

        // Encodes deltas the same way the decoder reads them (zig-zag, base-64, terminal high digit).
        private static string PeEncode(params (double dx, double dy)[] deltas)
        {
            var sb = new System.Text.StringBuilder("PE");
            foreach (var (dx, dy) in deltas) { sb.Append(PeVal(dx)); sb.Append(PeVal(dy)); }
            return sb.Append(';').ToString();
        }
        private static string PeVal(double v)
        {
            long zig = v < 0 ? ((long)(-v) << 1) | 1 : (long)v << 1;
            var sb = new System.Text.StringBuilder();
            while (zig >= 64) { sb.Append((char)(63 + (int)(zig & 63))); zig >>= 6; }
            return sb.Append((char)(63 + 64 + (int)zig)).ToString(); // terminal char
        }

        [Fact]
        public void EncodedPolyline_PE_DecodesRelativeMovesIntoAConnectedLine()
        {
            // A right-then-up step from the origin: two connected pen-down segments => one polyline.
            string pe = PeEncode((4000, 0), (0, 4000));
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA1000,1000;" + pe);
            Assert.Equal(1, Polylines(svg));
            Assert.Equal(3, Vertices(svg)); // start + 2 steps
        }

        [Fact]
        public void EncodedPolyline_PE_HandlesNegativeDeltas()
        {
            string pe = PeEncode((4000, 0), (-2000, 3000));
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PA5000,1000;" + pe);
            Assert.Contains("<path", svg);
            Assert.Equal(3, Vertices(svg));
        }

        // ---- graceful handling of non-geometry / interactive commands ------

        [Fact]
        public void InteractiveAndEscapeCommands_AreIgnored_GeometryStillRenders()
        {
            // Status-output (OS/OE/OA/OI), digitize (DC), pen dynamics (VS), page (PG), and an
            // ESC device-control sequence are interleaved with a real vector - none should break it.
            string hpgl = "IN;SP1;OS;OE;OA;OI;VS10;DC;.(;PU0,0;PD9000,0;.);PG;";
            string svg = HpglRenderer.RenderToSvg(hpgl);
            Assert.Contains("<path", svg);     // the PD vector survived
            int polylines = svg.Count(c => c == 'M');
            Assert.Equal(1, polylines);            // and nothing spurious was drawn
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
            Assert.DoesNotContain("<path", svg);
        }

        // ---- label / text subsystem (issue #8 §3.6) ------------------------

        private const string Etx = "";

        [Fact]
        public void Label_IsDrawnAsVectorStrokes_NotSystemText()
        {
            // Labels render through the built-in single-stroke font, so they are <path> strokes
            // and never <text>. "AB" has multiple strokes.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PU100,100;LBAB" + Etx + ";");
            Assert.Contains("<path", svg);
            Assert.DoesNotContain("<text", svg);
            Assert.True(Polylines(svg) >= 3, "two letters should yield several strokes; got " + Polylines(svg));
        }

        [Fact]
        public void Label_CellPitch_IsMonospacedAndMatchesKe5fx()
        {
            // HP-GL character cells are fixed-pitch: every glyph advances by the same cell. The
            // pitch reproduces the character-cell grid HP instruments place annotations on (#30):
            // the per-character advance is ~1.375x the character width (Advance/Em = 5.5/4.0). 'M'
            // fills the glyph ink (grid x 0..4 = one character width), so pitch / 'M'-width =
            // Advance/4 ~= 1.375. Earlier 1.5x/1.25x put characters off the grid so cross-row
            // columns (e.g. "CENTER" over "*RBW") no longer lined up.
            // Both labels share one render (identical auto-fit transform): a single 'M' (top band)
            // measures the glyph width; ten 'M's (bottom band) measure nine pitches + one width,
            // so pitch = (w10 - w1) / 9.
            var opt = new HpglRenderOptions { Width = 400, Height = 400, Background = HpglBackground.Black, Antialias = false };
            using (var bmp = HpglRenderer.RenderToBitmap(
                "IN;SP1;SI0.5,0.5;PU2000,4000;LBM" + Etx + ";PU2000,2000;LBMMMMMMMMMM" + Etx + ";", opt))
            {
                var bands = RowBands(bmp);
                Assert.Equal(2, bands.Count);          // the two labels are vertically separated
                int w1 = bands[0];                     // top band: single 'M' (one glyph em)
                int w10 = bands[1];                    // bottom band: ten 'M' (9 pitches + one em)
                double pitch = (w10 - w1) / 9.0;
                double ratio = pitch / w1;
                Assert.InRange(ratio, 1.28, 1.47);     // pitch / em-ink = Advance/4 ~= 1.375
            }
        }

        /// <summary>Widths (px) of each contiguous band of non-black rows, top to bottom.</summary>
        private static System.Collections.Generic.List<int> RowBands(Bitmap bmp)
        {
            var widths = new System.Collections.Generic.List<int>();
            int black = Color.Black.ToArgb();
            int minX = int.MaxValue, maxX = -1; bool inBand = false;
            for (int y = 0; y < bmp.Height; y++)
            {
                int rMin = int.MaxValue, rMax = -1;
                for (int x = 0; x < bmp.Width; x++)
                    if (bmp.GetPixel(x, y).ToArgb() != black) { if (x < rMin) rMin = x; if (x > rMax) rMax = x; }
                bool rowHasInk = rMax >= 0;
                if (rowHasInk) { inBand = true; if (rMin < minX) minX = rMin; if (rMax > maxX) maxX = rMax; }
                else if (inBand) { widths.Add(maxX - minX + 1); minX = int.MaxValue; maxX = -1; inBand = false; }
            }
            if (inBand) widths.Add(maxX - minX + 1);
            return widths;
        }

        [Fact]
        public void Label_Slant_SL_ChangesRendering()
        {
            string upright = HpglRenderer.RenderToSvg("IN;SP1;PU100,100;LBN" + Etx + ";");
            string slanted = HpglRenderer.RenderToSvg("IN;SP1;SL1;PU100,100;LBN" + Etx + ";");
            Assert.NotEqual(upright, slanted);
        }

        [Fact]
        public void Label_MultiLine_CrLf_RendersWithoutThrowing()
        {
            string svg = HpglRenderer.RenderToSvg("IN;SP1;PU100,100;LBAA\r\nBB" + Etx + ";");
            Assert.Contains("<path", svg);
        }

        [Fact]
        public void SymbolMode_SM_PlotsGlyphAtEachPoint()
        {
            // '*' is a three-stroke glyph; plotted at three points => several stroke polylines.
            string svg = HpglRenderer.RenderToSvg("IN;SP1;SM*;PU0,0;PA1000,0;PA2000,0;SM;");
            Assert.True(Polylines(svg) >= 3, "symbols should be drawn at each point; got " + Polylines(svg));
        }

        // ---- rotation (RO) and soft-clip window (IW) (issue #8 §2.4/§2.5) ---

        [Fact]
        public void Rotation_RO90_ChangesOrientation()
        {
            var opt = new HpglRenderOptions { Width = 400, Height = 400 };
            string flat = HpglRenderer.RenderToSvg("IN;SP1;PU0,0;PD1000,0;", opt);
            string rotated = HpglRenderer.RenderToSvg("IN;SP1;RO90;PU0,0;PD1000,0;", opt);
            Assert.NotEqual(flat, rotated);
        }

        [Fact]
        public void Window_IW_DropsSegmentsFullyOutsideIt()
        {
            // Two separate segments; the window keeps only the first - the second is clipped away.
            string body = "PU0,0;PD1000,1000;PU5000,5000;PD6000,6000;";
            string both = HpglRenderer.RenderToSvg("IN;SP1;" + body);
            string clipped = HpglRenderer.RenderToSvg("IN;SP1;IW0,0,1500,1500;" + body);
            Assert.Equal(2, Polylines(both));
            Assert.Equal(1, Polylines(clipped));
        }

        [Fact]
        public void Window_IW_Reset_RestoresFullDrawing()
        {
            // IW with no parameters clears the window, so a later segment outside the old window draws.
            string body = "PU0,0;PD1000,1000;IW;PU5000,5000;PD6000,6000;";
            string svg = HpglRenderer.RenderToSvg("IN;SP1;IW0,0,1500,1500;" + body);
            Assert.Equal(2, Polylines(svg));
        }
    }
}
