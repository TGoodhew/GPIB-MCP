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
// capture profile, not here. It covers the primitive set emitted by HP 8560/8566
// -class spectrum analyzers: IN/DF, IP/SC/IW, SP, PU/PD/PA/PR, LB/DT/SI/SR/DI.
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
                    case "DF": state.Reset(); break;
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
                    // LT/IW/PG and other non-geometry ops are ignored for layout in v1.
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

        public void Reset()
        {
            _scaled = false;
            Absolute = true;
            CharHeightUnits = 150;
            DirCos = 1; DirSin = 0;
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

        public GdiSink(Graphics g, PlotTransform t, HpglRenderOptions opt) { _g = g; _t = t; _opt = opt; }

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            _g.DrawLine(PenFor(pen), _t.MapX(x1), _t.MapY(y1), _t.MapX(x2), _t.MapY(y2));
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
        private int _pen = int.MinValue;
        private int _lastX, _lastY;
        private bool _open;

        public SvgSink(StringBuilder sb, PlotTransform t, HpglRenderOptions opt) { _sb = sb; _t = t; _opt = opt; }

        public void Line(double x1, double y1, double x2, double y2, int pen)
        {
            int ax = R(_t.MapX(x1)), ay = R(_t.MapY(y1));
            int bx = R(_t.MapX(x2)), by = R(_t.MapY(y2));

            if (_open && pen == _pen && ax == _lastX && ay == _lastY)
            {
                _points.Append(' ').Append(bx).Append(',').Append(by);
            }
            else
            {
                Flush();
                _pen = pen;
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
               .Append("\" stroke-width=\"1\" points=\"").Append(_points).Append("\"/>\n");
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
