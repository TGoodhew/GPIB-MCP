// -----------------------------------------------------------------------------
// Hpgl.Rendering - HP-GL/2 vector-to-bitmap renderer (.NET Framework 4.7.2).
//
// The HP-GL plotter-emulation capture-and-render technique that motivates this
// library is derived from the HP7470A Plotter Emulator (7470.cpp) by John Miles,
// KE5FX. Original C++ author: John Miles (KE5FX) - http://www.ke5fx.com/
// This independent C# adaptation carries no warranty from KE5FX.
//
// This is a clean, general HP-GL/2 vector renderer. Unlike 7470.cpp it contains
// no per-instrument fix-ups; instrument-specific quirks belong in the caller's
// capture profile, not here. Covered so far (issue #8): IN/DF, IP/SC, RO, IW
// (soft-clip), SP, PU/PD/PA/PR, arcs/circles/wedges (CI/AA/AR/EW) and edge
// rectangles (EA/ER) via chord subdivision, line types (LT) as dash patterns,
// area fill (RA/RR/WG, FT/PT) - solid via polygon fill, hatch as line spans -
// 7550A polygons (PM/EP/FP) with even-odd multi-contour fill, and the full
// label/text subsystem (LB/DT/SI/SR/SL/DI/DR/CP/ES/SM, CR/LF, mirroring,
// CS/CA/SS/SA + shift-in/out) drawn from a built-in single-stroke vector font.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>Renders HP-GL/2 vector text to a raster (<see cref="Bitmap"/> or PNG bytes).</summary>
    public static class HpglRenderer
    {
        /// <summary>Renders HP-GL/2 text to a <see cref="Bitmap"/>. Caller owns/disposes the bitmap.</summary>
        public static Bitmap RenderToBitmap(string hpgl, HpglRenderOptions options = null)
        {
            options = options ?? new HpglRenderOptions();
            var instructions = HpglParser.Parse(hpgl ?? string.Empty);

            // Pass 1: measure the drawn extent so the transform can auto-fit it.
            var measure = new MeasureSink();
            Execute(instructions, measure);

            var bmp = new Bitmap(options.Width, options.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = options.Antialias ? SmoothingMode.AntiAlias : SmoothingMode.None;
                g.Clear(options.ResolveBackground());

                if (measure.HasExtent)
                {
                    var transform = PlotTransform.Fit(measure, options);
                    using (var draw = new GdiSink(g, transform, options))
                        Execute(instructions, draw);
                }
            }
            return bmp;
        }

        /// <summary>Renders HP-GL/2 text and encodes the result as a PNG byte array.</summary>
        public static byte[] RenderToPng(string hpgl, HpglRenderOptions options = null)
        {
            using (var bmp = RenderToBitmap(hpgl, options))
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        /// <summary>Renders raw HP-GL/2 bytes (decoded as Latin-1) to PNG.</summary>
        public static byte[] RenderToPng(byte[] hpglBytes, HpglRenderOptions options = null) =>
            RenderToPng(DecodeLatin1(hpglBytes), options);

        /// <summary>
        /// Renders HP-GL/2 text to a self-contained SVG document (a string). The SVG uses the
        /// same auto-fit transform and pen palette as the raster path, but stays vector and
        /// compact (consecutive connected segments are merged into a single &lt;polyline&gt;).
        /// This is the form that can be shown inline in a chat as an SVG artifact.
        /// </summary>
        public static string RenderToSvg(string hpgl, HpglRenderOptions options = null)
        {
            options = options ?? new HpglRenderOptions();
            var instructions = HpglParser.Parse(hpgl ?? string.Empty);

            var measure = new MeasureSink();
            Execute(instructions, measure);

            var sb = new StringBuilder();
            sb.Append("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"").Append(options.Width)
              .Append("\" height=\"").Append(options.Height)
              .Append("\" viewBox=\"0 0 ").Append(options.Width).Append(' ').Append(options.Height)
              .Append("\">\n");
            sb.Append("<rect width=\"").Append(options.Width).Append("\" height=\"").Append(options.Height)
              .Append("\" fill=\"").Append(SvgSink.ToHex(options.ResolveBackground())).Append("\"/>\n");

            if (measure.HasExtent)
            {
                var transform = PlotTransform.Fit(measure, options);
                var sink = new SvgSink(sb, transform, options);
                Execute(instructions, sink);
                sink.Flush();
            }

            sb.Append("</svg>");
            return sb.ToString();
        }

        /// <summary>Renders raw HP-GL/2 bytes (decoded as Latin-1) to an SVG document string.</summary>
        public static string RenderToSvg(byte[] hpglBytes, HpglRenderOptions options = null) =>
            RenderToSvg(DecodeLatin1(hpglBytes), options);

        private static string DecodeLatin1(byte[] bytes) =>
            bytes == null ? string.Empty : Encoding.GetEncoding("ISO-8859-1").GetString(bytes);

        // ---------------------------------------------------------------------
        // Execution: replay the instruction list against a sink (measure or draw).
        // Everything is funnelled through a ClipSink so the IW soft-clip window
        // applies uniformly to vectors, fills, and label strokes.
        // ---------------------------------------------------------------------

        private static void Execute(IList<HpglInstruction> instructions, IPlotSink rawSink)
        {
            var state = new PlotterState();
            var sink = new ClipSink(rawSink, state);
            PolygonRecorder recorder = null;   // non-null while in polygon mode (PM0..PM2)
            foreach (var instruction in instructions)
            {
                // While in polygon mode, geometry records into the buffer instead of drawing.
                IPlotSink geom = recorder ?? (IPlotSink)sink;
                switch (instruction.Mnemonic)
                {
                    case "IN":
                    case "DF": state.Reset(); sink.SetLineType(HpglLineTypes.Solid); recorder = null; break;
                    case "PM": recorder = PolygonMode(instruction.Parameters, state, recorder); break;
                    case "EP": EdgePolygon(state, sink); break;
                    case "FP": FillPolygonBuffer(state, sink); break;
                    case "LT": sink.SetLineType(instruction.Parameters.Count > 0
                                   ? (int)instruction.Parameters[0] : HpglLineTypes.Solid); break;
                    case "SP": state.Pen = instruction.Parameters.Count > 0 ? (int)instruction.Parameters[0] : 0; break;
                    case "PU": state.PenDown = false; Move(state, instruction.Parameters, geom); break;
                    case "PD": state.PenDown = true; Move(state, instruction.Parameters, geom); break;
                    case "PA": state.Absolute = true; Move(state, instruction.Parameters, geom); break;
                    case "PR": state.Absolute = false; Move(state, instruction.Parameters, geom); break;
                    case "SC": state.SetScale(instruction.Parameters); break;
                    case "IP": state.SetInputPoints(instruction.Parameters); break;
                    case "RO": state.SetRotation(instruction.Parameters); break;
                    case "IW": state.SetWindow(instruction.Parameters); break;
                    // ---- label / text subsystem ----
                    case "SI": state.SetCharSizeCm(instruction.Parameters); break;
                    case "SR": state.SetCharSizeRelative(instruction.Parameters); break;
                    case "SL": state.SetSlant(instruction.Parameters); break;
                    case "DI": state.SetDirection(instruction.Parameters); break;
                    case "DR": state.SetDirectionRelative(instruction.Parameters); break;
                    case "ES": state.SetExtraSpace(instruction.Parameters); break;
                    case "CP": CharPlot(state, instruction.Parameters); break;
                    case "CS": state.StandardSet = instruction.Parameters.Count > 0 ? (int)instruction.Parameters[0] : 0; break;
                    case "CA": state.AlternateSet = instruction.Parameters.Count > 0 ? (int)instruction.Parameters[0] : 0; break;
                    case "SS": state.ActiveAlternate = false; break;
                    case "SA": state.ActiveAlternate = true; break;
                    case "SM": state.SymbolChar = !string.IsNullOrEmpty(instruction.Text) ? instruction.Text[0] : -1; break;
                    case "LB": DrawLabel(state, instruction.Text, sink); break;
                    case "DT": break; // label terminator is resolved by the parser
                    case "CT": if (instruction.Parameters.Count > 0) { /* chord mode (deg/dev) - degrees only */ } break;
                    case "CI": Circle(state, instruction.Parameters, geom); break;
                    case "AA": Arc(state, instruction.Parameters, geom, relativeCenter: false); break;
                    case "AR": Arc(state, instruction.Parameters, geom, relativeCenter: true); break;
                    case "EA": EdgeRect(state, instruction.Parameters, geom, relative: false); break;
                    case "ER": EdgeRect(state, instruction.Parameters, geom, relative: true); break;
                    case "EW": EdgeWedge(state, instruction.Parameters, geom); break;
                    case "FT": state.SetFill(instruction.Parameters); break;
                    case "PT": state.SetPenThickness(instruction.Parameters); break;
                    case "RA": FillRect(state, instruction.Parameters, geom, relative: false); break;
                    case "RR": FillRect(state, instruction.Parameters, geom, relative: true); break;
                    case "WG": FillWedge(state, instruction.Parameters, geom); break;
                }
            }
        }

        private static void Move(PlotterState state, IReadOnlyList<double> p, IPlotSink sink)
        {
            for (int k = 0; k + 1 < p.Count; k += 2)
            {
                double nx, ny;
                state.NextPosition(p[k], p[k + 1], out nx, out ny);
                if (state.PenDown) sink.Line(state.X, state.Y, nx, ny, state.Pen);
                state.X = nx;
                state.Y = ny;
                if (state.SymbolChar >= 0) DrawSymbol(state, state.SymbolChar, nx, ny, sink);
            }
        }

        // ---------------------------------------------------------------------
        // Label / symbol rendering. Glyphs come from the built-in single-stroke
        // font (StrokeFont) and are emitted as Line() calls, so they honour size
        // (SI/SR), slant (SL), direction (DI/DR), rotation (RO), clipping (IW),
        // pen colour and line type exactly like every other vector.
        // ---------------------------------------------------------------------

        /// <summary>Resolves the rotated text baseline ("along") and perpendicular ("up") unit vectors.</summary>
        private static void RotatedDir(PlotterState s, out double ax, out double ay, out double ux, out double uy)
        {
            double cx = s.DirCos, cy = s.DirSin;
            s.RotateVector(ref cx, ref cy);
            ax = cx; ay = cy; ux = -cy; uy = cx;
        }

        /// <summary>LB: draws a string from the current position, advancing the carry-over cursor.</summary>
        private static void DrawLabel(PlotterState s, string text, IPlotSink sink)
        {
            if (string.IsNullOrEmpty(text)) return;
            double ax, ay, ux, uy; RotatedDir(s, out ax, out ay, out ux, out uy);
            double sx = s.CharWidthUnits / StrokeFont.Advance;
            double sy = s.CharHeightUnits / StrokeFont.Cap;
            double ox = s.X, oy = s.Y, cursorX = 0, lineY = 0;
            double adv = s.AdvanceXUnits, lineStep = s.LineAdvanceUnits;
            bool activeAlt = s.ActiveAlternate;

            foreach (char ch in text)
            {
                if (ch == '\r') { cursorX = 0; continue; }              // CR -> line origin
                if (ch == '\n') { cursorX = 0; lineY -= lineStep; continue; } // LF -> next line
                if (ch == '') { activeAlt = true; continue; }   // shift-out -> alternate set
                if (ch == '') { activeAlt = false; continue; }  // shift-in  -> standard set

                int[][] glyph = StrokeFont.Get(ch);
                if (glyph != null)
                {
                    foreach (var stroke in glyph)
                        for (int k = 0; k + 3 < stroke.Length; k += 2)
                        {
                            double l0x = stroke[k] * sx + s.SlantTan * (stroke[k + 1] * sy) + cursorX;
                            double l0y = stroke[k + 1] * sy + lineY;
                            double l1x = stroke[k + 2] * sx + s.SlantTan * (stroke[k + 3] * sy) + cursorX;
                            double l1y = stroke[k + 3] * sy + lineY;
                            sink.Line(ox + l0x * ax + l0y * ux, oy + l0x * ay + l0y * uy,
                                      ox + l1x * ax + l1y * ux, oy + l1x * ay + l1y * uy, s.Pen);
                        }
                }
                cursorX += adv;
            }
            // Carry-over cursor: the pen ends at the position following the last character.
            s.X = ox + cursorX * ax + lineY * ux;
            s.Y = oy + cursorX * ay + lineY * uy;
            s.ActiveAlternate = activeAlt;
        }

        /// <summary>SM: draws the symbol glyph centred on a plotted point (no cursor advance).</summary>
        private static void DrawSymbol(PlotterState s, int chCode, double px, double py, IPlotSink sink)
        {
            int[][] glyph = StrokeFont.Get((char)chCode);
            if (glyph == null) return;
            double ax, ay, ux, uy; RotatedDir(s, out ax, out ay, out ux, out uy);
            double sx = s.CharWidthUnits / StrokeFont.Advance;
            double sy = s.CharHeightUnits / StrokeFont.Cap;
            const double cgx = 2, cgy = 3; // approximate glyph centre in grid units

            foreach (var stroke in glyph)
                for (int k = 0; k + 3 < stroke.Length; k += 2)
                {
                    double l0x = (stroke[k] - cgx) * sx + s.SlantTan * ((stroke[k + 1] - cgy) * sy);
                    double l0y = (stroke[k + 1] - cgy) * sy;
                    double l1x = (stroke[k + 2] - cgx) * sx + s.SlantTan * ((stroke[k + 3] - cgy) * sy);
                    double l1y = (stroke[k + 3] - cgy) * sy;
                    sink.Line(px + l0x * ax + l0y * ux, py + l0x * ay + l0y * uy,
                              px + l1x * ax + l1y * ux, py + l1x * ay + l1y * uy, s.Pen);
                }
        }

        /// <summary>CP spaces,lines: moves the cursor by N character cells without drawing (CP; = CR+LF).</summary>
        private static void CharPlot(PlotterState s, IReadOnlyList<double> p)
        {
            double ax, ay, ux, uy; RotatedDir(s, out ax, out ay, out ux, out uy);
            double dCols, dLines;
            if (p.Count >= 2) { dCols = p[0]; dLines = p[1]; }
            else { dCols = 0; dLines = -1; } // CP; -> down one line
            double dx = dCols * s.AdvanceXUnits, dy = dLines * s.LineAdvanceUnits;
            s.X += dx * ax + dy * ux;
            s.Y += dx * ay + dy * uy;
        }

        // ---------------------------------------------------------------------
        // Curves & rectangles. These draw regardless of pen up/down (HP-GL treats
        // them as draw commands) and decompose into the same Line sink as vectors,
        // so both the measure and draw passes see them with no sink changes.
        // ---------------------------------------------------------------------

        private const double DegToRad = Math.PI / 180.0;

        /// <summary>CI radius[,chord]: circle centred on the current position; pen position unchanged.</summary>
        private static void Circle(PlotterState s, IReadOnlyList<double> p, IPlotSink sink)
        {
            if (p.Count < 1) return;
            double r = Math.Abs(p[0]);
            double chord = p.Count >= 2 ? p[1] : s.ChordAngleDeg;
            double ex, ey;
            EmitArc(sink, s.X, s.Y, r * s.ScaleX, r * s.ScaleY, 0, 360, chord, s.Pen, out ex, out ey);
        }

        /// <summary>AA/AR X,Y,sweep[,chord]: arc about the (abs/rel) centre from the current position.</summary>
        private static void Arc(PlotterState s, IReadOnlyList<double> p, IPlotSink sink, bool relativeCenter)
        {
            if (p.Count < 3) return;
            double cx, cy;
            if (relativeCenter) { double dx, dy; s.DeltaToPlot(p[0], p[1], out dx, out dy); cx = s.X + dx; cy = s.Y + dy; }
            else s.UserToPlot(p[0], p[1], out cx, out cy);

            double sweep = p[2];
            double chord = p.Count >= 4 ? p[3] : s.ChordAngleDeg;
            double vx = s.X - cx, vy = s.Y - cy;
            double r = Math.Sqrt(vx * vx + vy * vy);
            double startDeg = Math.Atan2(vy, vx) / DegToRad;

            double ex, ey;
            EmitArc(sink, cx, cy, r, r, startDeg, sweep, chord, s.Pen, out ex, out ey);
            s.X = ex; s.Y = ey; // the pen ends at the arc endpoint
        }

        /// <summary>EA/ER X,Y: rectangle between the current position and the (abs/rel) opposite corner.</summary>
        private static void EdgeRect(PlotterState s, IReadOnlyList<double> p, IPlotSink sink, bool relative)
        {
            if (p.Count < 2) return;
            double x2, y2;
            if (relative) { double dx, dy; s.DeltaToPlot(p[0], p[1], out dx, out dy); x2 = s.X + dx; y2 = s.Y + dy; }
            else s.UserToPlot(p[0], p[1], out x2, out y2);

            double x1 = s.X, y1 = s.Y;
            sink.Line(x1, y1, x2, y1, s.Pen);
            sink.Line(x2, y1, x2, y2, s.Pen);
            sink.Line(x2, y2, x1, y2, s.Pen);
            sink.Line(x1, y2, x1, y1, s.Pen);
            // pen position unchanged
        }

        /// <summary>EW radius,start,sweep[,chord]: wedge outline (two radii + arc) about the current position.</summary>
        private static void EdgeWedge(PlotterState s, IReadOnlyList<double> p, IPlotSink sink)
        {
            if (p.Count < 3) return;
            double r = Math.Abs(p[0]);
            double startDeg = p[1] + s.RotationDeg, sweep = p[2];
            double chord = p.Count >= 4 ? p[3] : s.ChordAngleDeg;
            double rx = r * s.ScaleX, ry = r * s.ScaleY;
            double cx = s.X, cy = s.Y;

            double ax = cx + rx * Math.Cos(startDeg * DegToRad), ay = cy + ry * Math.Sin(startDeg * DegToRad);
            sink.Line(cx, cy, ax, ay, s.Pen);            // centre -> arc start
            double ex, ey;
            EmitArc(sink, cx, cy, rx, ry, startDeg, sweep, chord, s.Pen, out ex, out ey);
            sink.Line(ex, ey, cx, cy, s.Pen);            // arc end -> centre
            // pen position unchanged (returns to centre)
        }

        /// <summary>
        /// Subdivides an arc/ellipse into chords and emits them as <see cref="IPlotSink.Line"/> calls.
        /// Steps by <paramref name="chordDeg"/> (HP-GL default 5°); reports the final point in
        /// <paramref name="endX"/>/<paramref name="endY"/>. rx/ry differ only under non-uniform SC scaling.
        /// </summary>
        private static void EmitArc(IPlotSink sink, double cx, double cy, double rx, double ry,
            double startDeg, double sweepDeg, double chordDeg, int pen, out double endX, out double endY)
        {
            chordDeg = Math.Abs(chordDeg);
            if (chordDeg < 0.1) chordDeg = 5.0;
            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweepDeg) / chordDeg));

            double prevX = cx + rx * Math.Cos(startDeg * DegToRad);
            double prevY = cy + ry * Math.Sin(startDeg * DegToRad);
            for (int k = 1; k <= steps; k++)
            {
                double a = (startDeg + sweepDeg * k / steps) * DegToRad;
                double x = cx + rx * Math.Cos(a);
                double y = cy + ry * Math.Sin(a);
                sink.Line(prevX, prevY, x, y, pen);
                prevX = x; prevY = y;
            }
            endX = prevX; endY = prevY;
        }

        // ---------------------------------------------------------------------
        // Area fill (FT/PT, RA/RR, WG). Solid fills (FT 1/2) use the sink's native
        // polygon fill (a valid raster/SVG preview shortcut per the spec); hatch
        // fills (FT 3 parallel, 4 cross-hatch) are generated as Line spans by a
        // scanline pass so they keep the characteristic plotted-hatch look.
        // ---------------------------------------------------------------------

        /// <summary>RA/RR X,Y: fill the rectangle between the current position and the (abs/rel) corner.</summary>
        private static void FillRect(PlotterState s, IReadOnlyList<double> p, IPlotSink sink, bool relative)
        {
            if (p.Count < 2) return;
            double x2, y2;
            if (relative) { double dx, dy; s.DeltaToPlot(p[0], p[1], out dx, out dy); x2 = s.X + dx; y2 = s.Y + dy; }
            else s.UserToPlot(p[0], p[1], out x2, out y2);
            double x1 = s.X, y1 = s.Y;
            var rect = new[] { new PointD(x1, y1), new PointD(x2, y1), new PointD(x2, y2), new PointD(x1, y2) };
            FillContours(s, sink, new[] { rect });
            // pen position unchanged
        }

        /// <summary>WG radius,start,sweep[,chord]: fill a pie wedge about the current position.</summary>
        private static void FillWedge(PlotterState s, IReadOnlyList<double> p, IPlotSink sink)
        {
            if (p.Count < 3) return;
            double r = Math.Abs(p[0]), startDeg = p[1] + s.RotationDeg, sweep = p[2];
            double chord = p.Count >= 4 ? p[3] : s.ChordAngleDeg;
            double rx = r * s.ScaleX, ry = r * s.ScaleY;
            double cx = s.X, cy = s.Y;

            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(0.1, Math.Abs(chord))));
            var pts = new List<PointD> { new PointD(cx, cy) };
            for (int k = 0; k <= steps; k++)
            {
                double a = (startDeg + sweep * k / steps) * DegToRad;
                pts.Add(new PointD(cx + rx * Math.Cos(a), cy + ry * Math.Sin(a)));
            }
            FillContours(s, sink, new[] { pts.ToArray() });
            // pen position unchanged (centre)
        }

        // ---- 7550A polygons (PM / EP / FP) ----------------------------------

        /// <summary>PM n: 0 = enter/clear the buffer, 1 = close sub-contour, 2 = exit (finalize onto state).</summary>
        private static PolygonRecorder PolygonMode(IReadOnlyList<double> p, PlotterState s, PolygonRecorder rec)
        {
            int mode = p.Count > 0 ? (int)p[0] : 0;
            if (mode == 0) return new PolygonRecorder();          // begin a fresh polygon buffer
            if (mode == 1) { rec?.Break(); return rec ?? new PolygonRecorder(); }
            if (rec != null) s.Polygon = rec.Finish();            // mode 2: finalize and exit
            return null;
        }

        /// <summary>EP: stroke the stored polygon outline (every sub-contour).</summary>
        private static void EdgePolygon(PlotterState s, IPlotSink sink)
        {
            if (s.Polygon == null) return;
            foreach (var c in s.Polygon)
                for (int i = 0; i + 1 < c.Length; i++)
                    sink.Line(c[i].X, c[i].Y, c[i + 1].X, c[i + 1].Y, s.Pen);
        }

        /// <summary>FP: fill the stored polygon with the current fill type (even-odd across sub-contours).</summary>
        private static void FillPolygonBuffer(PlotterState s, IPlotSink sink)
        {
            if (s.Polygon != null) FillContours(s, sink, s.Polygon);
        }

        /// <summary>
        /// Fills one or more closed contours per the current fill type: solid (FT 1/2) via the sink's
        /// native polygon fill; hatch (FT 3 parallel, 4 cross-hatch) as Line spans. Multiple contours
        /// are filled even-odd together (so 7550A polygons with holes hatch correctly).
        /// </summary>
        private static void FillContours(PlotterState s, IPlotSink sink, IReadOnlyList<PointD[]> contours)
        {
            if (contours.Count == 0) return;
            if (s.FillType == 3 || s.FillType == 4)
            {
                double spacing = s.FillSpacingUnits > 0 ? s.FillSpacingUnits : 0.01 * s.FrameDiagonal;
                if (spacing <= 0) spacing = 1;
                EmitHatch(sink, contours, s.FillAngleDeg, spacing, s.Pen);
                if (s.FillType == 4) EmitHatch(sink, contours, s.FillAngleDeg + 90, spacing, s.Pen);
            }
            else
            {
                foreach (var c in contours)
                {
                    if (c.Length < 3) continue;
                    var xs = new double[c.Length]; var ys = new double[c.Length];
                    for (int i = 0; i < c.Length; i++) { xs[i] = c[i].X; ys[i] = c[i].Y; }
                    sink.FillPolygon(xs, ys, s.Pen); // FT 1/2 (and shading) -> solid (holes not subtracted)
                }
            }
        }

        /// <summary>
        /// Scanline hatch over one or more contours: parallel fill lines at <paramref name="angleDeg"/>
        /// spaced by <paramref name="spacing"/>, clipped to the contours (even-odd across all of them),
        /// emitted as Line spans. Rotates into a frame where hatch lines are horizontal, scans, rotates back.
        /// </summary>
        private static void EmitHatch(IPlotSink sink, IReadOnlyList<PointD[]> contours,
            double angleDeg, double spacing, int pen)
        {
            if (spacing <= 0) return;
            double ca = Math.Cos(-angleDeg * DegToRad), sa = Math.Sin(-angleDeg * DegToRad);

            var rot = new List<PointD[]>(contours.Count);
            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var c in contours)
            {
                if (c.Length < 3) continue;
                var rc = new PointD[c.Length];
                for (int i = 0; i < c.Length; i++)
                {
                    double ry = c[i].X * sa + c[i].Y * ca;
                    rc[i] = new PointD(c[i].X * ca - c[i].Y * sa, ry);
                    if (ry < minY) minY = ry;
                    if (ry > maxY) maxY = ry;
                }
                rot.Add(rc);
            }
            if (rot.Count == 0) return;

            double back = angleDeg * DegToRad;
            double cb = Math.Cos(back), sb = Math.Sin(back);
            var crossings = new List<double>();
            int guard = 0;
            for (double yy = minY + spacing / 2.0; yy < maxY && guard < 200000; yy += spacing, guard++)
            {
                crossings.Clear();
                foreach (var rc in rot)
                {
                    int n = rc.Length;
                    for (int i = 0; i < n; i++)
                    {
                        int j = (i + 1) % n;
                        double y0 = rc[i].Y, y1 = rc[j].Y;
                        if ((y0 <= yy && y1 > yy) || (y1 <= yy && y0 > yy))
                        {
                            double tt = (yy - y0) / (y1 - y0);
                            crossings.Add(rc[i].X + tt * (rc[j].X - rc[i].X));
                        }
                    }
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    double xa = crossings[k], xb = crossings[k + 1];
                    sink.Line(xa * cb - yy * sb, xa * sb + yy * cb,
                              xb * cb - yy * sb, xb * sb + yy * cb, pen);
                }
            }
        }
    }

    // -------------------------------------------------------------------------
    // Plotter state machine. Coordinates are tracked in "plot units"; SC user
    // scaling (if present) maps user coordinates into a fixed plot-unit frame,
    // and RO rotates the emitted plot-unit coordinates. The final auto-fit
    // transform normalizes whatever range results.
    // -------------------------------------------------------------------------

    /// <summary>A point in plot-unit space.</summary>
    internal struct PointD
    {
        public double X, Y;
        public PointD(double x, double y) { X = x; Y = y; }
    }

    internal sealed class PlotterState
    {
        /// <summary>The finalized 7550A polygon buffer (PM2), one entry per sub-contour. Null when none.</summary>
        public List<PointD[]> Polygon;

        // Default input-point frame; only the proportions matter (auto-fit normalizes).
        private double _ipX1 = 0, _ipY1 = 0, _ipX2 = 10000, _ipY2 = 10000;
        private double _scXmin, _scXmax, _scYmin, _scYmax;
        private bool _scaled;

        public double X, Y;
        public bool PenDown;
        public bool Absolute = true;
        public int Pen = 1;

        // Coordinate-system rotation (RO): 0/90/180/270 degrees, applied about the origin.
        public int RotationDeg;

        // Soft-clip window (IW), in plot units (already rotated); null window when !HasClip.
        public bool HasClip;
        public double ClipX1, ClipY1, ClipX2, ClipY2;

        // Character/label state. Width/height are signed (negative = mirrored).
        public double CharWidthUnits = 150;
        public double CharHeightUnits = 150;
        public double SlantTan;
        public double ExtraSpaceX, ExtraSpaceY;
        public double DirCos = 1, DirSin = 0;
        public int StandardSet, AlternateSet;
        public bool ActiveAlternate;
        public int SymbolChar = -1;          // -1 = symbol mode off
        public double ChordAngleDeg = 5.0;   // arc/circle subdivision step (HP-GL default 5°)

        // Area-fill state (FT/PT). Type 1/2 = solid, 3 = parallel hatch, 4 = cross-hatch.
        public int FillType = 1;
        public double FillSpacingUnits = 0;  // 0 => default (1% of the P1-P2 diagonal)
        public double FillAngleDeg = 0;
        public double PenThicknessMm = 0.3;

        public void Reset()
        {
            _scaled = false;
            Absolute = true;
            RotationDeg = 0;
            HasClip = false;
            CharWidthUnits = 150; CharHeightUnits = 150;
            SlantTan = 0; ExtraSpaceX = 0; ExtraSpaceY = 0;
            DirCos = 1; DirSin = 0;
            StandardSet = 0; AlternateSet = 0; ActiveAlternate = false;
            SymbolChar = -1;
            ChordAngleDeg = 5.0;
            FillType = 1; FillSpacingUnits = 0; FillAngleDeg = 0; PenThicknessMm = 0.3;
            Polygon = null;
        }

        /// <summary>Diagonal of the P1-P2 (IP) frame in plot units - the basis for default hatch spacing.</summary>
        public double FrameDiagonal =>
            Math.Sqrt((_ipX2 - _ipX1) * (_ipX2 - _ipX1) + (_ipY2 - _ipY1) * (_ipY2 - _ipY1));

        public double FrameWidth => Math.Abs(_ipX2 - _ipX1);
        public double FrameHeight => Math.Abs(_ipY2 - _ipY1);

        /// <summary>Per-character cursor advance (plot units), including ES extra space; signed for mirroring.</summary>
        public double AdvanceXUnits => CharWidthUnits * (1 + ExtraSpaceX);

        /// <summary>Per-line advance (plot units): one cell height (= 2x char height) plus ES extra line space.</summary>
        public double LineAdvanceUnits => 2 * Math.Abs(CharHeightUnits) * (1 + ExtraSpaceY);

        public void SetFill(IReadOnlyList<double> p)
        {
            if (p.Count == 0) { FillType = 1; FillSpacingUnits = 0; FillAngleDeg = 0; return; }
            FillType = (int)p[0];
            FillSpacingUnits = p.Count >= 2 ? Math.Abs(p[1]) * Math.Max(ScaleX, ScaleY) : 0;
            FillAngleDeg = p.Count >= 3 ? p[2] : 0;
        }

        public void SetPenThickness(IReadOnlyList<double> p)
        {
            if (p.Count >= 1 && p[0] > 0) PenThicknessMm = p[0];
        }

        /// <summary>Plot-units per user-unit on each axis (1 when scaling is off). Non-uniform under SC.</summary>
        public double ScaleX => _scaled ? (_ipX2 - _ipX1) / (_scXmax - _scXmin) : 1.0;
        public double ScaleY => _scaled ? (_ipY2 - _ipY1) / (_scYmax - _scYmin) : 1.0;

        /// <summary>Maps a user/plotter coordinate pair to plot units (honours SC scaling and RO rotation).</summary>
        public void UserToPlot(double ux, double uy, out double px, out double py)
        {
            double x = UserToPlotX(ux), y = UserToPlotY(uy);
            RotateVector(ref x, ref y);
            px = x; py = y;
        }

        /// <summary>Maps a relative coordinate delta to plot units (honours SC scaling and RO rotation).</summary>
        public void DeltaToPlot(double dx, double dy, out double px, out double py)
        {
            double x = DeltaToPlotX(dx), y = DeltaToPlotY(dy);
            RotateVector(ref x, ref y);
            px = x; py = y;
        }

        /// <summary>Rotates a plot-unit vector/point about the origin by the current RO angle.</summary>
        public void RotateVector(ref double x, ref double y)
        {
            switch (RotationDeg)
            {
                case 90: { double t = x; x = -y; y = t; break; }
                case 180: { x = -x; y = -y; break; }
                case 270: { double t = x; x = y; y = -t; break; }
            }
        }

        public void SetInputPoints(IReadOnlyList<double> p)
        {
            if (p.Count >= 4) { _ipX1 = p[0]; _ipY1 = p[1]; _ipX2 = p[2]; _ipY2 = p[3]; }
        }

        public void SetScale(IReadOnlyList<double> p)
        {
            if (p.Count >= 4)
            {
                _scXmin = p[0]; _scXmax = p[1]; _scYmin = p[2]; _scYmax = p[3];
                _scaled = _scXmax != _scXmin && _scYmax != _scYmin;
            }
            else
            {
                _scaled = false; // SC; with no args turns scaling off
            }
        }

        public void SetRotation(IReadOnlyList<double> p)
        {
            int deg = p.Count > 0 ? (int)Math.Round(p[0] / 90.0) * 90 : 0;
            RotationDeg = ((deg % 360) + 360) % 360;
        }

        /// <summary>IW X1,Y1,X2,Y2: set the soft-clip window (user coords). IW; resets it.</summary>
        public void SetWindow(IReadOnlyList<double> p)
        {
            if (p.Count < 4) { HasClip = false; return; }
            double ax, ay, bx, by;
            UserToPlot(p[0], p[1], out ax, out ay);
            UserToPlot(p[2], p[3], out bx, out by);
            ClipX1 = Math.Min(ax, bx); ClipX2 = Math.Max(ax, bx);
            ClipY1 = Math.Min(ay, by); ClipY2 = Math.Max(ay, by);
            HasClip = true;
        }

        /// <summary>Computes the next plot-unit position for an absolute/relative coordinate pair.</summary>
        public void NextPosition(double a, double b, out double nx, out double ny)
        {
            if (Absolute)
            {
                UserToPlot(a, b, out nx, out ny);
            }
            else
            {
                double dx, dy;
                DeltaToPlot(a, b, out dx, out dy);
                nx = X + dx; ny = Y + dy;
            }
        }

        private double UserToPlotX(double ux) =>
            _scaled ? _ipX1 + (ux - _scXmin) * (_ipX2 - _ipX1) / (_scXmax - _scXmin) : ux;

        private double UserToPlotY(double uy) =>
            _scaled ? _ipY1 + (uy - _scYmin) * (_ipY2 - _ipY1) / (_scYmax - _scYmin) : uy;

        private double DeltaToPlotX(double dx) =>
            _scaled ? dx * (_ipX2 - _ipX1) / (_scXmax - _scXmin) : dx;

        private double DeltaToPlotY(double dy) =>
            _scaled ? dy * (_ipY2 - _ipY1) / (_scYmax - _scYmin) : dy;

        public void SetCharSizeCm(IReadOnlyList<double> p)
        {
            // SI width,height in centimetres; 1 plot unit = 0.025 mm => 400 units/cm. Sign = mirror.
            if (p.Count >= 2) { CharWidthUnits = p[0] * 400.0; CharHeightUnits = p[1] * 400.0; }
            else { CharWidthUnits = 150; CharHeightUnits = 150; } // SI; -> size default
        }

        public void SetCharSizeRelative(IReadOnlyList<double> p)
        {
            // SR width,height as a percentage of the IP frame. SR; -> 0.75% x 1.5%.
            if (p.Count >= 2) { CharWidthUnits = p[0] / 100.0 * FrameWidth; CharHeightUnits = p[1] / 100.0 * FrameHeight; }
            else { CharWidthUnits = 0.0075 * FrameWidth; CharHeightUnits = 0.015 * FrameHeight; }
        }

        public void SetSlant(IReadOnlyList<double> p) => SlantTan = p.Count >= 1 ? p[0] : 0;

        public void SetExtraSpace(IReadOnlyList<double> p)
        {
            ExtraSpaceX = p.Count >= 1 ? p[0] : 0;
            ExtraSpaceY = p.Count >= 2 ? p[1] : 0;
        }

        public void SetDirection(IReadOnlyList<double> p)
        {
            if (p.Count >= 2) Normalize(p[0], p[1]);
            else { DirCos = 1; DirSin = 0; }
        }

        public void SetDirectionRelative(IReadOnlyList<double> p)
        {
            // DR run,rise are fractions of the P1-P2 frame; scale before normalizing.
            if (p.Count >= 2) Normalize(p[0] * FrameWidth, p[1] * FrameHeight);
            else { DirCos = 1; DirSin = 0; }
        }

        private void Normalize(double run, double rise)
        {
            double mag = Math.Sqrt(run * run + rise * rise);
            if (mag > 0) { DirCos = run / mag; DirSin = rise / mag; }
            else { DirCos = 1; DirSin = 0; }
        }
    }

    // -------------------------------------------------------------------------
    // Sinks: pass 1 measures the extent; pass 2 draws with the fitted transform.
    // -------------------------------------------------------------------------

    internal interface IPlotSink
    {
        void Line(double x1, double y1, double x2, double y2, int pen);
        /// <summary>Sets the current line type for subsequent vectors (-1 = solid; 0-6 = HP-GL patterns).</summary>
        void SetLineType(int lineType);
        /// <summary>Solid-fills a closed polygon (plot-unit vertices) - used for FT type 1/2 fills.</summary>
        void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen);
    }

    /// <summary>
    /// Applies the IW soft-clip window to everything drawn: line segments are clipped with
    /// Liang-Barsky and filled polygons with Sutherland-Hodgman, against the current window read
    /// live from <see cref="PlotterState"/> (so a mid-stream IW change takes effect immediately).
    /// Pass-through when no window is set.
    /// </summary>
    internal sealed class ClipSink : IPlotSink
    {
        private readonly IPlotSink _inner;
        private readonly PlotterState _s;

        public ClipSink(IPlotSink inner, PlotterState s) { _inner = inner; _s = s; }

        public void SetLineType(int lineType) => _inner.SetLineType(lineType);

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            if (!_s.HasClip) { _inner.Line(x1, y1, x2, y2, pen); return; }
            if (ClipSegment(ref x1, ref y1, ref x2, ref y2))
                _inner.Line(x1, y1, x2, y2, pen);
        }

        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen)
        {
            if (!_s.HasClip) { _inner.FillPolygon(xs, ys, pen); return; }
            var clipped = ClipPolygon(xs, ys);
            if (clipped.Count >= 3)
            {
                var cx = new double[clipped.Count]; var cy = new double[clipped.Count];
                for (int i = 0; i < clipped.Count; i++) { cx[i] = clipped[i].X; cy[i] = clipped[i].Y; }
                _inner.FillPolygon(cx, cy, pen);
            }
        }

        /// <summary>Liang-Barsky clip of a segment to the window; returns false if fully outside.</summary>
        private bool ClipSegment(ref double x0, ref double y0, ref double x1, ref double y1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            double t0 = 0, t1 = 1;
            double[] p = { -dx, dx, -dy, dy };
            double[] q = { x0 - _s.ClipX1, _s.ClipX2 - x0, y0 - _s.ClipY1, _s.ClipY2 - y0 };
            for (int i = 0; i < 4; i++)
            {
                if (p[i] == 0) { if (q[i] < 0) return false; }
                else
                {
                    double t = q[i] / p[i];
                    if (p[i] < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
                    else { if (t < t0) return false; if (t < t1) t1 = t; }
                }
            }
            double nx0 = x0 + t0 * dx, ny0 = y0 + t0 * dy;
            double nx1 = x0 + t1 * dx, ny1 = y0 + t1 * dy;
            x0 = nx0; y0 = ny0; x1 = nx1; y1 = ny1;
            return true;
        }

        /// <summary>Sutherland-Hodgman clip of a polygon to the rectangular window.</summary>
        private List<PointD> ClipPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys)
        {
            var poly = new List<PointD>(xs.Count);
            for (int i = 0; i < xs.Count; i++) poly.Add(new PointD(xs[i], ys[i]));
            poly = ClipEdge(poly, 0, _s.ClipX1);   // left:   x >= X1
            poly = ClipEdge(poly, 1, _s.ClipX2);   // right:  x <= X2
            poly = ClipEdge(poly, 2, _s.ClipY1);   // bottom: y >= Y1
            poly = ClipEdge(poly, 3, _s.ClipY2);   // top:    y <= Y2
            return poly;
        }

        private static bool Inside(PointD pt, int edge, double v)
        {
            switch (edge)
            {
                case 0: return pt.X >= v;
                case 1: return pt.X <= v;
                case 2: return pt.Y >= v;
                default: return pt.Y <= v;
            }
        }

        private static PointD Intersect(PointD a, PointD b, int edge, double v)
        {
            double t;
            if (edge <= 1) t = (v - a.X) / (b.X - a.X);
            else t = (v - a.Y) / (b.Y - a.Y);
            return new PointD(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        }

        private static List<PointD> ClipEdge(List<PointD> input, int edge, double v)
        {
            var output = new List<PointD>();
            if (input.Count == 0) return output;
            for (int i = 0; i < input.Count; i++)
            {
                PointD cur = input[i], prev = input[(i + input.Count - 1) % input.Count];
                bool curIn = Inside(cur, edge, v), prevIn = Inside(prev, edge, v);
                if (curIn)
                {
                    if (!prevIn) output.Add(Intersect(prev, cur, edge, v));
                    output.Add(cur);
                }
                else if (prevIn)
                {
                    output.Add(Intersect(prev, cur, edge, v));
                }
            }
            return output;
        }
    }

    /// <summary>
    /// HP-GL line-type (LT) patterns rendered as dash arrays. Each pattern is a sequence of
    /// on/off run lengths; the full repeat is scaled to a fraction of the canvas diagonal (HP-GL
    /// default 4% of the P1-P2 diagonal), so dashes look right at any output size.
    /// </summary>
    internal static class HpglLineTypes
    {
        // Sentinel distinct from every real line type (-6..6); negative reals are adaptive variants.
        public const int Solid = int.MinValue;
        public const double DefaultLengthFraction = 0.04;

        // Relative on/off run lengths per LT (LT0 ≈ dots; LT1-6 dashes/dash-dots).
        private static readonly Dictionary<int, double[]> Patterns = new Dictionary<int, double[]>
        {
            [0] = new[] { 0.5, 2.0 },
            [1] = new[] { 1.0, 2.0 },
            [2] = new[] { 3.0, 3.0 },
            [3] = new[] { 6.0, 3.0 },
            [4] = new[] { 6.0, 3.0, 1.0, 3.0 },
            [5] = new[] { 6.0, 3.0, 1.0, 3.0, 1.0, 3.0 },
            [6] = new[] { 3.0, 3.0, 1.0, 3.0 },
        };

        /// <summary>Returns the scaled dash run-lengths (pixels) for a line type, or null for solid.</summary>
        public static float[] DashArray(int lineType, double canvasDiagonalPx)
        {
            if (lineType == Solid) return null;
            double[] pat;
            if (!Patterns.TryGetValue(Math.Abs(lineType), out pat)) return null; // solid / unknown
            double sum = 0; foreach (var v in pat) sum += v;
            double unit = DefaultLengthFraction * canvasDiagonalPx / sum;
            var arr = new float[pat.Length];
            for (int i = 0; i < pat.Length; i++) arr[i] = (float)Math.Max(0.5, pat[i] * unit);
            return arr;
        }
    }

    /// <summary>
    /// Captures geometry as polygon contours while in 7550A polygon mode (PM0..PM2). It is an
    /// <see cref="IPlotSink"/>, so the existing Move/arc/rectangle helpers record into it unchanged:
    /// each <see cref="Line"/> appends to the current contour, and a discontinuity (the segment does
    /// not start where the last ended - e.g. after a pen-up move) begins a new contour. <see cref="Break"/>
    /// forces a new contour for PM1.
    /// </summary>
    internal sealed class PolygonRecorder : IPlotSink
    {
        public readonly List<List<PointD>> Contours = new List<List<PointD>>();
        private List<PointD> _cur;
        private bool _break = true;
        private double _lx, _ly;
        private bool _have;

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            if (_break || !_have || x1 != _lx || y1 != _ly)
            {
                _cur = new List<PointD> { new PointD(x1, y1) };
                Contours.Add(_cur);
                _break = false;
            }
            _cur.Add(new PointD(x2, y2));
            _lx = x2; _ly = y2; _have = true;
        }

        /// <summary>Ends the current contour so the next vector starts a fresh one (PM1).</summary>
        public void Break() { _break = true; _have = false; }

        /// <summary>The finalized buffer: contours with at least an edge, as arrays.</summary>
        public List<PointD[]> Finish()
        {
            var result = new List<PointD[]>();
            foreach (var c in Contours)
                if (c.Count >= 2) result.Add(c.ToArray());
            return result;
        }

        public void SetLineType(int lineType) { }
        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen) { }
    }

    internal sealed class MeasureSink : IPlotSink
    {
        public double MinX = double.MaxValue, MinY = double.MaxValue;
        public double MaxX = double.MinValue, MaxY = double.MinValue;
        public bool HasExtent => MaxX >= MinX && MaxY >= MinY;

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            Include(x1, y1); Include(x2, y2);
        }

        public void SetLineType(int lineType) { /* line style does not affect extent */ }

        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen)
        {
            for (int i = 0; i < xs.Count; i++) Include(xs[i], ys[i]);
        }

        private void Include(double x, double y)
        {
            if (x < MinX) MinX = x;
            if (y < MinY) MinY = y;
            if (x > MaxX) MaxX = x;
            if (y > MaxY) MaxY = y;
        }
    }

    internal sealed class PlotTransform
    {
        private readonly double _scale, _srcMinX, _srcMinY, _offX, _offY;
        private readonly int _height;

        public double Scale => _scale;

        private PlotTransform(double scale, double srcMinX, double srcMinY, double offX, double offY, int height)
        {
            _scale = scale; _srcMinX = srcMinX; _srcMinY = srcMinY; _offX = offX; _offY = offY; _height = height;
        }

        public static PlotTransform Fit(MeasureSink extent, HpglRenderOptions opt)
        {
            double w = Math.Max(1, extent.MaxX - extent.MinX);
            double h = Math.Max(1, extent.MaxY - extent.MinY);
            double availW = Math.Max(1, opt.Width - 2 * opt.Margin);
            double availH = Math.Max(1, opt.Height - 2 * opt.Margin);
            double scale = Math.Min(availW / w, availH / h);

            double offX = opt.Margin + (availW - w * scale) / 2.0;
            double offY = opt.Margin + (availH - h * scale) / 2.0;
            return new PlotTransform(scale, extent.MinX, extent.MinY, offX, offY, opt.Height);
        }

        public float MapX(double x) => (float)(_offX + (x - _srcMinX) * _scale);

        // HP-GL Y increases upward; raster Y increases downward, so flip.
        public float MapY(double y) => (float)(_height - (_offY + (y - _srcMinY) * _scale));
    }

    internal sealed class GdiSink : IPlotSink, IDisposable
    {
        private readonly Graphics _g;
        private readonly PlotTransform _t;
        private readonly HpglRenderOptions _opt;
        private readonly Dictionary<int, Pen> _pens = new Dictionary<int, Pen>();
        private readonly double _diag;
        private int _lineType = HpglLineTypes.Solid;

        public GdiSink(Graphics g, PlotTransform t, HpglRenderOptions opt)
        {
            _g = g; _t = t; _opt = opt;
            _diag = Math.Sqrt((double)opt.Width * opt.Width + (double)opt.Height * opt.Height);
        }

        public void SetLineType(int lineType) { _lineType = lineType; }

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            _g.DrawLine(PenFor(pen), _t.MapX(x1), _t.MapY(y1), _t.MapX(x2), _t.MapY(y2));
        }

        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen)
        {
            if (xs.Count < 3) return;
            var pts = new PointF[xs.Count];
            for (int i = 0; i < xs.Count; i++) pts[i] = new PointF(_t.MapX(xs[i]), _t.MapY(ys[i]));
            using (var brush = new SolidBrush(_opt.ResolvePen(pen)))
                _g.FillPolygon(brush, pts);
        }

        private Pen PenFor(int pen)
        {
            Pen p;
            if (!_pens.TryGetValue(pen, out p))
            {
                p = new Pen(_opt.ResolvePen(pen), 1f);
                _pens[pen] = p;
            }
            // Apply the current line type. (Cached pen mutated per draw - safe single-threaded.)
            float[] dash = HpglLineTypes.DashArray(_lineType, _diag);
            if (dash == null) p.DashStyle = DashStyle.Solid;
            else p.DashPattern = dash;
            return p;
        }

        public void Dispose()
        {
            foreach (var p in _pens.Values) p.Dispose();
            _pens.Clear();
        }
    }

    /// <summary>
    /// Emits SVG elements for the fitted plot. Consecutive pen-down segments that share a pen
    /// and join end-to-end are coalesced into one &lt;polyline&gt; so a dense trace stays small.
    /// Coordinates are rounded to whole pixels - sub-pixel precision is invisible at screen sizes
    /// and would only bloat the document.
    /// </summary>
    internal sealed class SvgSink : IPlotSink
    {
        private readonly StringBuilder _sb;
        private readonly PlotTransform _t;
        private readonly HpglRenderOptions _opt;

        private readonly StringBuilder _points = new StringBuilder();
        private readonly double _diag;
        private int _pen = int.MinValue;
        private int _lineType = HpglLineTypes.Solid;
        private int _runLineType = HpglLineTypes.Solid;
        private int _lastX, _lastY;
        private bool _open;

        public SvgSink(StringBuilder sb, PlotTransform t, HpglRenderOptions opt)
        {
            _sb = sb; _t = t; _opt = opt;
            _diag = Math.Sqrt((double)opt.Width * opt.Width + (double)opt.Height * opt.Height);
        }

        public void SetLineType(int lineType) { _lineType = lineType; }

        public void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen)
        {
            if (xs.Count < 3) return;
            Flush(); // paint the fill beneath any subsequent strokes
            _sb.Append("<polygon fill=\"").Append(ToHex(_opt.ResolvePen(pen))).Append("\" points=\"");
            for (int i = 0; i < xs.Count; i++)
            {
                if (i > 0) _sb.Append(' ');
                _sb.Append(R(_t.MapX(xs[i]))).Append(',').Append(R(_t.MapY(ys[i])));
            }
            _sb.Append("\"/>\n");
        }

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            int ax = R(_t.MapX(x1)), ay = R(_t.MapY(y1));
            int bx = R(_t.MapX(x2)), by = R(_t.MapY(y2));

            // Coalesce only when pen, line type, and join point all match.
            if (_open && pen == _pen && _lineType == _runLineType && ax == _lastX && ay == _lastY)
            {
                _points.Append(' ').Append(bx).Append(',').Append(by);
            }
            else
            {
                Flush();
                _pen = pen;
                _runLineType = _lineType;
                _points.Append(ax).Append(',').Append(ay).Append(' ').Append(bx).Append(',').Append(by);
                _open = true;
            }
            _lastX = bx; _lastY = by;
        }

        /// <summary>Writes the pending polyline (if any) and resets the batch.</summary>
        public void Flush()
        {
            if (!_open) return;
            _sb.Append("<polyline fill=\"none\" stroke=\"").Append(ToHex(_opt.ResolvePen(_pen)))
               .Append("\" stroke-width=\"1\"");
            float[] dash = HpglLineTypes.DashArray(_runLineType, _diag);
            if (dash != null)
            {
                _sb.Append(" stroke-dasharray=\"");
                for (int i = 0; i < dash.Length; i++)
                {
                    if (i > 0) _sb.Append(',');
                    _sb.Append(R(dash[i]));
                }
                _sb.Append('"');
            }
            _sb.Append(" points=\"").Append(_points).Append("\"/>\n");
            _points.Clear();
            _open = false;
        }

        private static int R(float v) => (int)Math.Round(v);

        internal static string ToHex(Color c) =>
            "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    }
}
