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
// capture profile, not here. Covered so far (issue #8): IN/DF, IP/SC, SP,
// PU/PD/PA/PR, LB/DT/SI/SR/DI, arcs/circles/wedges (CI/AA/AR/EW) and edge
// rectangles (EA/ER) via chord subdivision, line types (LT) as dash patterns,
// and area fill (RA/RR/WG, FT/PT) - solid via polygon fill, hatch as line spans.
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
        // ---------------------------------------------------------------------

        private static void Execute(IList<HpglInstruction> instructions, IPlotSink sink)
        {
            var state = new PlotterState();
            foreach (var instruction in instructions)
            {
                switch (instruction.Mnemonic)
                {
                    case "IN":
                    case "DF": state.Reset(); sink.SetLineType(HpglLineTypes.Solid); break;
                    case "LT": sink.SetLineType(instruction.Parameters.Count > 0
                                   ? (int)instruction.Parameters[0] : HpglLineTypes.Solid); break;
                    case "SP": state.Pen = instruction.Parameters.Count > 0 ? (int)instruction.Parameters[0] : 0; break;
                    case "PU": state.PenDown = false; Move(state, instruction.Parameters, sink); break;
                    case "PD": state.PenDown = true; Move(state, instruction.Parameters, sink); break;
                    case "PA": state.Absolute = true; Move(state, instruction.Parameters, sink); break;
                    case "PR": state.Absolute = false; Move(state, instruction.Parameters, sink); break;
                    case "SC": state.SetScale(instruction.Parameters); break;
                    case "IP": state.SetInputPoints(instruction.Parameters); break;
                    case "SI": state.SetCharSizeCm(instruction.Parameters); break;
                    case "SR": state.SetCharSizeRelative(instruction.Parameters); break;
                    case "DI": state.SetDirection(instruction.Parameters); break;
                    case "LB": sink.Label(state, instruction.Text); break;
                    case "CT": if (instruction.Parameters.Count > 0) { /* chord mode (deg/dev) - degrees only */ } break;
                    case "CI": Circle(state, instruction.Parameters, sink); break;
                    case "AA": Arc(state, instruction.Parameters, sink, relativeCenter: false); break;
                    case "AR": Arc(state, instruction.Parameters, sink, relativeCenter: true); break;
                    case "EA": EdgeRect(state, instruction.Parameters, sink, relative: false); break;
                    case "ER": EdgeRect(state, instruction.Parameters, sink, relative: true); break;
                    case "EW": EdgeWedge(state, instruction.Parameters, sink); break;
                    case "FT": state.SetFill(instruction.Parameters); break;
                    case "PT": state.SetPenThickness(instruction.Parameters); break;
                    case "RA": FillRect(state, instruction.Parameters, sink, relative: false); break;
                    case "RR": FillRect(state, instruction.Parameters, sink, relative: true); break;
                    case "WG": FillWedge(state, instruction.Parameters, sink); break;
                    // IW/PG and other non-geometry ops are ignored for layout in v1.
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
            }
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
            if (relativeCenter) { cx = s.X + s.DeltaX(p[0]); cy = s.Y + s.DeltaY(p[1]); }
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
            if (relative) { x2 = s.X + s.DeltaX(p[0]); y2 = s.Y + s.DeltaY(p[1]); }
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
            double startDeg = p[1], sweep = p[2];
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
            if (relative) { x2 = s.X + s.DeltaX(p[0]); y2 = s.Y + s.DeltaY(p[1]); }
            else s.UserToPlot(p[0], p[1], out x2, out y2);
            double x1 = s.X, y1 = s.Y;
            FillRegion(s, sink, new[] { x1, x2, x2, x1 }, new[] { y1, y1, y2, y2 });
            // pen position unchanged
        }

        /// <summary>WG radius,start,sweep[,chord]: fill a pie wedge about the current position.</summary>
        private static void FillWedge(PlotterState s, IReadOnlyList<double> p, IPlotSink sink)
        {
            if (p.Count < 3) return;
            double r = Math.Abs(p[0]), startDeg = p[1], sweep = p[2];
            double chord = p.Count >= 4 ? p[3] : s.ChordAngleDeg;
            double rx = r * s.ScaleX, ry = r * s.ScaleY;
            double cx = s.X, cy = s.Y;

            int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(sweep) / Math.Max(0.1, Math.Abs(chord))));
            var xs = new List<double> { cx };
            var ys = new List<double> { cy };
            for (int k = 0; k <= steps; k++)
            {
                double a = (startDeg + sweep * k / steps) * DegToRad;
                xs.Add(cx + rx * Math.Cos(a));
                ys.Add(cy + ry * Math.Sin(a));
            }
            FillRegion(s, sink, xs.ToArray(), ys.ToArray());
            // pen position unchanged (centre)
        }

        /// <summary>Fills a closed polygon per the current fill type: solid via the sink, else hatch spans.</summary>
        private static void FillRegion(PlotterState s, IPlotSink sink, double[] xs, double[] ys)
        {
            int t = s.FillType;
            if (t == 3 || t == 4)
            {
                double spacing = s.FillSpacingUnits > 0 ? s.FillSpacingUnits : 0.01 * s.FrameDiagonal;
                if (spacing <= 0) spacing = 1;
                EmitHatch(sink, xs, ys, s.FillAngleDeg, spacing, s.Pen);
                if (t == 4) EmitHatch(sink, xs, ys, s.FillAngleDeg + 90, spacing, s.Pen); // cross-hatch
            }
            else
            {
                sink.FillPolygon(xs, ys, s.Pen); // FT 1/2 (and shading) -> solid
            }
        }

        /// <summary>
        /// Scanline hatch: emits parallel fill lines at <paramref name="angleDeg"/> spaced by
        /// <paramref name="spacing"/> across the polygon, clipped to it (even-odd), as Line spans.
        /// Works by rotating into a frame where the hatch lines are horizontal, scanning, then rotating back.
        /// </summary>
        private static void EmitHatch(IPlotSink sink, double[] xs, double[] ys,
            double angleDeg, double spacing, int pen)
        {
            int n = xs.Length;
            if (n < 3 || spacing <= 0) return;
            double ca = Math.Cos(-angleDeg * DegToRad), sa = Math.Sin(-angleDeg * DegToRad);

            // Rotate vertices into the hatch frame (hatch lines become horizontal).
            var rx = new double[n];
            var ry = new double[n];
            double minY = double.MaxValue, maxY = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                rx[i] = xs[i] * ca - ys[i] * sa;
                ry[i] = xs[i] * sa + ys[i] * ca;
                if (ry[i] < minY) minY = ry[i];
                if (ry[i] > maxY) maxY = ry[i];
            }

            double back = angleDeg * DegToRad; // rotate spans back to world space
            double cb = Math.Cos(back), sb = Math.Sin(back);
            var crossings = new List<double>();
            int guard = 0;
            for (double yy = minY + spacing / 2.0; yy < maxY && guard < 100000; yy += spacing, guard++)
            {
                crossings.Clear();
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    double y0 = ry[i], y1 = ry[j];
                    if ((y0 <= yy && y1 > yy) || (y1 <= yy && y0 > yy))
                    {
                        double tt = (yy - y0) / (y1 - y0);
                        crossings.Add(rx[i] + tt * (rx[j] - rx[i]));
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
    // scaling (if present) maps user coordinates into a fixed plot-unit frame.
    // The final auto-fit transform normalizes whatever range results.
    // -------------------------------------------------------------------------

    internal sealed class PlotterState
    {
        // Default input-point frame; only the proportions matter (auto-fit normalizes).
        private double _ipX1 = 0, _ipY1 = 0, _ipX2 = 10000, _ipY2 = 10000;
        private double _scXmin, _scXmax, _scYmin, _scYmax;
        private bool _scaled;

        public double X, Y;
        public bool PenDown;
        public bool Absolute = true;
        public int Pen = 1;

        public double CharHeightUnits = 150; // sensible default until SI/SR seen
        public double DirCos = 1, DirSin = 0;
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
            CharHeightUnits = 150;
            DirCos = 1; DirSin = 0;
            ChordAngleDeg = 5.0;
            FillType = 1; FillSpacingUnits = 0; FillAngleDeg = 0; PenThicknessMm = 0.3;
        }

        /// <summary>Diagonal of the P1-P2 (IP) frame in plot units - the basis for default hatch spacing.</summary>
        public double FrameDiagonal =>
            Math.Sqrt((_ipX2 - _ipX1) * (_ipX2 - _ipX1) + (_ipY2 - _ipY1) * (_ipY2 - _ipY1));

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

        /// <summary>Maps a user/plotter coordinate pair to plot units (honours SC scaling).</summary>
        public void UserToPlot(double ux, double uy, out double px, out double py)
        {
            px = UserToPlotX(ux); py = UserToPlotY(uy);
        }

        /// <summary>Maps a relative coordinate delta to plot units (honours SC scaling).</summary>
        public double DeltaX(double dx) => DeltaToPlotX(dx);
        public double DeltaY(double dy) => DeltaToPlotY(dy);

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

        /// <summary>Computes the next plot-unit position for an absolute/relative coordinate pair.</summary>
        public void NextPosition(double a, double b, out double nx, out double ny)
        {
            if (Absolute)
            {
                nx = UserToPlotX(a);
                ny = UserToPlotY(b);
            }
            else
            {
                nx = X + DeltaToPlotX(a);
                ny = Y + DeltaToPlotY(b);
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
            // SI width,height in centimetres; 1 plot unit = 0.025 mm => 400 units/cm.
            if (p.Count >= 2) CharHeightUnits = Math.Abs(p[1]) * 400.0;
        }

        public void SetCharSizeRelative(IReadOnlyList<double> p)
        {
            // SR width,height as a percentage of the IP frame.
            if (p.Count >= 2) CharHeightUnits = Math.Abs(p[1]) / 100.0 * Math.Abs(_ipY2 - _ipY1);
        }

        public void SetDirection(IReadOnlyList<double> p)
        {
            if (p.Count >= 2)
            {
                double run = p[0], rise = p[1];
                double mag = Math.Sqrt(run * run + rise * rise);
                if (mag > 0) { DirCos = run / mag; DirSin = rise / mag; }
            }
            else { DirCos = 1; DirSin = 0; }
        }
    }

    // -------------------------------------------------------------------------
    // Sinks: pass 1 measures the extent; pass 2 draws with the fitted transform.
    // -------------------------------------------------------------------------

    internal interface IPlotSink
    {
        void Line(double x1, double y1, double x2, double y2, int pen);
        void Label(PlotterState state, string text);
        /// <summary>Sets the current line type for subsequent vectors (-1 = solid; 0-6 = HP-GL patterns).</summary>
        void SetLineType(int lineType);
        /// <summary>Solid-fills a closed polygon (plot-unit vertices) - used for FT type 1/2 fills.</summary>
        void FillPolygon(IReadOnlyList<double> xs, IReadOnlyList<double> ys, int pen);
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

        public void Label(PlotterState state, string text)
        {
            // Include the label's text extent, not just its baseline anchor: text rises above
            // the baseline (by ~cap height) and runs along the label direction. Omitting this
            // makes the auto-fit too tight and clips edge annotations (e.g. the top row).
            Include(state.X, state.Y);
            if (string.IsNullOrEmpty(text)) return;

            double h = state.CharHeightUnits;
            double w = text.Length * state.CharHeightUnits * 0.6; // approx average glyph advance
            double cos = state.DirCos, sin = state.DirSin;        // text direction
            double upX = -sin, upY = cos;                          // perpendicular "up" (cap height)

            Include(state.X + w * cos, state.Y + w * sin);                       // baseline end
            Include(state.X + h * upX, state.Y + h * upY);                       // above anchor
            Include(state.X + w * cos + h * upX, state.Y + w * sin + h * upY);   // far top corner
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
        private readonly FontFamily _fontFamily = FontFamily.GenericSansSerif;
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

        public void Label(PlotterState state, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            float px = _t.MapX(state.X);
            float py = _t.MapY(state.Y);
            float heightPx = (float)Math.Max(6.0, state.CharHeightUnits * _t.Scale);

            // DI direction -> on-screen angle (Y is flipped, so negate the rise).
            double angleDeg = Math.Atan2(-state.DirSin, state.DirCos) * 180.0 / Math.PI;

            using (var font = new Font(_fontFamily, heightPx, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(_opt.ResolvePen(state.Pen)))
            {
                var saved = _g.Save();
                _g.TranslateTransform(px, py);
                _g.RotateTransform((float)angleDeg);
                // HP-GL label baseline sits at the pen position; nudge up by the cap height.
                _g.DrawString(text, font, brush, 0, -heightPx);
                _g.Restore(saved);
            }
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

        public void Label(PlotterState state, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            Flush(); // keep label text painted after the geometry around it

            int px = R(_t.MapX(state.X)), py = R(_t.MapY(state.Y));
            int size = (int)Math.Max(6.0, state.CharHeightUnits * _t.Scale);
            double angleDeg = Math.Atan2(-state.DirSin, state.DirCos) * 180.0 / Math.PI;

            _sb.Append("<text x=\"").Append(px).Append("\" y=\"").Append(py)
               .Append("\" fill=\"").Append(ToHex(_opt.ResolvePen(state.Pen)))
               .Append("\" font-family=\"sans-serif\" font-size=\"").Append(size).Append("px\"");
            if (Math.Abs(angleDeg) > 0.01)
                _sb.Append(" transform=\"rotate(").Append(R((float)angleDeg))
                   .Append(' ').Append(px).Append(' ').Append(py).Append(")\"");
            _sb.Append('>').Append(Escape(text)).Append("</text>\n");
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

        private static string Escape(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '&': sb.Append("&amp;"); break;
                    case '<': sb.Append("&lt;"); break;
                    case '>': sb.Append("&gt;"); break;
                    case '"': sb.Append("&quot;"); break;
                    default:
                        // Drop control characters (e.g. a stray label terminator) that are illegal in XML.
                        if (c >= ' ' || c == '\t') sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }
}
