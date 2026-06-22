// -----------------------------------------------------------------------------
// Hpgl.Rendering - HP-GL/2 vector-to-bitmap renderer (.NET Framework 4.7.2).
//
// The HP-GL plotter-emulation capture-and-render technique that motivates this
// library is derived from the HP7470A Plotter Emulator (7470.cpp) by John Miles,
// KE5FX. Original C++ author: John Miles (KE5FX) - http://www.ke5fx.com/
// This independent C# adaptation carries no warranty from KE5FX.
// -----------------------------------------------------------------------------

using System.Drawing;

namespace Hpgl.Rendering
{
    /// <summary>Background fill for the rendered raster.</summary>
    public enum HpglBackground
    {
        /// <summary>Black canvas (matches an instrument CRT; best for screen viewing).</summary>
        Black,
        /// <summary>White canvas (best for printing).</summary>
        White
    }

    /// <summary>
    /// Options controlling how HP-GL/2 is rasterized by <see cref="HpglRenderer"/>.
    /// All fields have sensible defaults; a default instance renders a 1024x768
    /// black-background image with an auto-fit, aspect-preserving transform.
    /// </summary>
    public sealed class HpglRenderOptions
    {
        /// <summary>Output bitmap width in pixels (default 1024).</summary>
        public int Width { get; set; } = 1024;

        /// <summary>Output bitmap height in pixels (default 768).</summary>
        public int Height { get; set; } = 768;

        /// <summary>Border (pixels) kept clear around the fitted plot (default 12).</summary>
        public int Margin { get; set; } = 12;

        /// <summary>Canvas background (default <see cref="HpglBackground.Black"/>).</summary>
        public HpglBackground Background { get; set; } = HpglBackground.Black;

        /// <summary>Antialias vectors and text (default true).</summary>
        public bool Antialias { get; set; } = true;

        /// <summary>
        /// SVG only: Ramer-Douglas-Peucker tolerance in pixels for simplifying LONG stroke runs
        /// (e.g. a spectrum trace) when emitting SVG. The default 0.5 is sub-pixel - visually lossless
        /// at the render resolution - and roughly halves trace size; short runs (font glyphs, graticule,
        /// circles, arcs) are never simplified. Set 0 to disable (byte-exact geometry). Does not affect
        /// the raster (PNG) path, which is always exact.
        /// </summary>
        public double SvgSimplifyTolerancePx { get; set; } = 0.5;

        /// <summary>
        /// SVG only: when true, render labels as compact &lt;text&gt; elements (a system monospace font)
        /// instead of the exact single-stroke HP plotter font. This is the "low fidelity" mode - the
        /// glyph shapes differ from a real 7475/7440/7550, but each label is ~one element instead of
        /// dozens of strokes, so the SVG (and the time to display it inline) shrinks substantially.
        /// Default false (high fidelity, single-stroke font). The raster (PNG) is always the stroke font.
        /// </summary>
        public bool SvgTextLabels { get; set; } = false;

        /// <summary>
        /// Optional explicit pen palette indexed by HP-GL pen number (SP n -&gt; PenColors[n % len]).
        /// When null, a readable default palette is chosen to suit <see cref="Background"/>.
        /// </summary>
        public Color[] PenColors { get; set; }

        internal Color ResolveBackground() =>
            Background == HpglBackground.White ? Color.White : Color.Black;

        private static readonly Color[] DefaultOnBlack =
        {
            Color.White, Color.White, Color.Cyan, Color.Lime,
            Color.Yellow, Color.Red, Color.Magenta, Color.DeepSkyBlue
        };

        private static readonly Color[] DefaultOnWhite =
        {
            Color.Black, Color.Black, Color.Blue, Color.Green,
            Color.DarkGoldenrod, Color.Red, Color.Magenta, Color.Navy
        };

        internal Color ResolvePen(int pen)
        {
            var palette = PenColors != null && PenColors.Length > 0
                ? PenColors
                : (Background == HpglBackground.White ? DefaultOnWhite : DefaultOnBlack);
            int index = ((pen % palette.Length) + palette.Length) % palette.Length;
            return palette[index];
        }
    }
}
