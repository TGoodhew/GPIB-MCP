// -----------------------------------------------------------------------------
// Hpgl.Rendering - single-stroke vector font for HP-GL labels (Set 0 / ASCII).
//
// The HP plotters' built-in glyph outlines are NOT published as coordinate tables
// (both programming manuals show them only as printed charts - see
// docs/HPGL-CharacterSet-Font-Reference.md). The reference therefore recommends
// substituting a single-stroke ("Hershey"-style) font of equivalent metrics. This
// is such a font: every printable ASCII glyph as one or more pen-down polylines on
// a fixed grid, so the renderer can draw real strokes that honour size, slant,
// direction, rotation, clipping, pen colour and line type uniformly with all other
// geometry (instead of falling back to a host system font).
//
// Grid: x 0..Em rightward, y 0 (baseline) .. Cap (capital height) upward;
// lowercase x-height ~4, descenders to -2. A glyph's drawn ink width is Em units;
// the cell advance is the wider Advance, so the glyph sits inside the cell with
// inter-character whitespace (gap = Advance-Em). The current HP-GL character WIDTH
// (SI/SR) maps to the glyph ink width Em - so the cell advance works out to
// Advance/Em (= 1.5x) the character width, matching real HP plotters / KE5FX.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Hpgl.Rendering
{
    /// <summary>A metric-matched single-stroke font for HP-GL Set 0 (ASCII) labels.</summary>
    internal static class StrokeFont
    {
        /// <summary>Capital height in grid units (maps to the current character height).</summary>
        public const int Cap = 6;

        /// <summary>Glyph ink em-width in grid units (maps to the current HP-GL character width).</summary>
        public const int Em = 4;

        /// <summary>Cell advance in grid units. Wider than <see cref="Em"/> so adjacent glyphs
        /// are separated by uniform inter-character whitespace (fixed-pitch / monospaced cells).</summary>
        public const int Advance = 6;

        /// <summary>Returns the glyph as a list of pen-down polylines (grid units), or null if undrawn.</summary>
        public static int[][] Get(char c)
        {
            int[][] g;
            return Glyphs.TryGetValue(c, out g) ? g : null;
        }

        // Each entry: an array of strokes; each stroke is a flat {x0,y0,x1,y1,...} polyline.
        private static readonly Dictionary<char, int[][]> Glyphs = new Dictionary<char, int[][]>
        {
            [' '] = new int[0][],

            // ---- digits ----
            ['0'] = new[] { new[] { 1,0, 3,0, 4,1, 4,5, 3,6, 1,6, 0,5, 0,1, 1,0 } },
            ['1'] = new[] { new[] { 1,5, 2,6, 2,0 }, new[] { 1,0, 3,0 } },
            ['2'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4, 0,0, 4,0 } },
            ['3'] = new[] { new[] { 0,6, 4,6, 2,3, 4,2, 4,1, 3,0, 1,0, 0,1 } },
            ['4'] = new[] { new[] { 3,0, 3,6, 0,2, 4,2 } },
            ['5'] = new[] { new[] { 4,6, 0,6, 0,3, 3,3, 4,2, 4,1, 3,0, 1,0, 0,1 } },
            ['6'] = new[] { new[] { 4,5, 3,6, 1,6, 0,5, 0,1, 1,0, 3,0, 4,1, 4,2, 3,3, 0,3 } },
            ['7'] = new[] { new[] { 0,6, 4,6, 1,0 } },
            ['8'] = new[] { new[] { 1,3, 0,4, 0,5, 1,6, 3,6, 4,5, 4,4, 3,3, 1,3, 0,2, 0,1, 1,0, 3,0, 4,1, 4,2, 3,3 } },
            ['9'] = new[] { new[] { 4,3, 1,3, 0,4, 0,5, 1,6, 3,6, 4,5, 4,1, 3,0, 1,0, 0,1 } },

            // ---- uppercase ----
            ['A'] = new[] { new[] { 0,0, 2,6, 4,0 }, new[] { 1,2, 3,2 } },
            ['B'] = new[] { new[] { 0,0, 0,6 }, new[] { 0,6, 3,6, 3,3, 0,3 }, new[] { 0,3, 4,3, 4,0, 0,0 } },
            ['C'] = new[] { new[] { 4,5, 3,6, 1,6, 0,5, 0,1, 1,0, 3,0, 4,1 } },
            ['D'] = new[] { new[] { 0,0, 0,6 }, new[] { 0,6, 2,6, 4,4, 4,2, 2,0, 0,0 } },
            ['E'] = new[] { new[] { 4,6, 0,6, 0,0, 4,0 }, new[] { 0,3, 3,3 } },
            ['F'] = new[] { new[] { 4,6, 0,6, 0,0 }, new[] { 0,3, 3,3 } },
            ['G'] = new[] { new[] { 4,5, 3,6, 1,6, 0,5, 0,1, 1,0, 3,0, 4,1, 4,3, 2,3 } },
            ['H'] = new[] { new[] { 0,0, 0,6 }, new[] { 4,0, 4,6 }, new[] { 0,3, 4,3 } },
            ['I'] = new[] { new[] { 1,6, 3,6 }, new[] { 2,6, 2,0 }, new[] { 1,0, 3,0 } },
            ['J'] = new[] { new[] { 3,6, 3,1, 2,0, 1,0, 0,1 } },
            ['K'] = new[] { new[] { 0,0, 0,6 }, new[] { 4,6, 0,3, 4,0 } },
            ['L'] = new[] { new[] { 0,6, 0,0, 4,0 } },
            ['M'] = new[] { new[] { 0,0, 0,6, 2,3, 4,6, 4,0 } },
            ['N'] = new[] { new[] { 0,0, 0,6, 4,0, 4,6 } },
            ['O'] = new[] { new[] { 1,0, 3,0, 4,1, 4,5, 3,6, 1,6, 0,5, 0,1, 1,0 } },
            ['P'] = new[] { new[] { 0,0, 0,6, 3,6, 4,5, 4,4, 3,3, 0,3 } },
            ['Q'] = new[] { new[] { 1,0, 3,0, 4,1, 4,5, 3,6, 1,6, 0,5, 0,1, 1,0 }, new[] { 2,2, 4,0 } },
            ['R'] = new[] { new[] { 0,0, 0,6, 3,6, 4,5, 4,4, 3,3, 0,3 }, new[] { 2,3, 4,0 } },
            ['S'] = new[] { new[] { 4,5, 3,6, 1,6, 0,5, 0,4, 1,3, 3,3, 4,2, 4,1, 3,0, 1,0, 0,1 } },
            ['T'] = new[] { new[] { 0,6, 4,6 }, new[] { 2,6, 2,0 } },
            ['U'] = new[] { new[] { 0,6, 0,1, 1,0, 3,0, 4,1, 4,6 } },
            ['V'] = new[] { new[] { 0,6, 2,0, 4,6 } },
            ['W'] = new[] { new[] { 0,6, 1,0, 2,3, 3,0, 4,6 } },
            ['X'] = new[] { new[] { 0,0, 4,6 }, new[] { 0,6, 4,0 } },
            ['Y'] = new[] { new[] { 0,6, 2,3, 4,6 }, new[] { 2,3, 2,0 } },
            ['Z'] = new[] { new[] { 0,6, 4,6, 0,0, 4,0 } },

            // ---- lowercase ----
            ['a'] = new[] { new[] { 4,0, 4,4 }, new[] { 4,3, 3,4, 1,4, 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['b'] = new[] { new[] { 0,6, 0,0 }, new[] { 0,1, 1,0, 3,0, 4,1, 4,3, 3,4, 1,4, 0,3 } },
            ['c'] = new[] { new[] { 4,3, 3,4, 1,4, 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['d'] = new[] { new[] { 4,6, 4,0 }, new[] { 4,1, 3,0, 1,0, 0,1, 0,3, 1,4, 3,4, 4,3 } },
            ['e'] = new[] { new[] { 0,2, 4,2, 4,3, 3,4, 1,4, 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['f'] = new[] { new[] { 1,0, 1,5, 2,6, 3,6 }, new[] { 0,4, 2,4 } },
            ['g'] = new[] { new[] { 4,4, 4,-1, 3,-2, 1,-2, 0,-1 }, new[] { 4,3, 3,4, 1,4, 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['h'] = new[] { new[] { 0,6, 0,0 }, new[] { 0,3, 1,4, 3,4, 4,3, 4,0 } },
            ['i'] = new[] { new[] { 2,4, 2,0 }, new[] { 2,5, 2,6 } },
            ['j'] = new[] { new[] { 3,4, 3,-1, 2,-2, 1,-2, 0,-1 }, new[] { 3,5, 3,6 } },
            ['k'] = new[] { new[] { 0,6, 0,0 }, new[] { 3,4, 0,2, 3,0 } },
            ['l'] = new[] { new[] { 2,6, 2,0 } },
            ['m'] = new[] { new[] { 0,0, 0,4 }, new[] { 0,3, 1,4, 2,3, 2,0 }, new[] { 2,3, 3,4, 4,3, 4,0 } },
            ['n'] = new[] { new[] { 0,0, 0,4 }, new[] { 0,3, 1,4, 3,4, 4,3, 4,0 } },
            ['o'] = new[] { new[] { 1,0, 3,0, 4,1, 4,3, 3,4, 1,4, 0,3, 0,1, 1,0 } },
            ['p'] = new[] { new[] { 0,-2, 0,4 }, new[] { 0,1, 1,0, 3,0, 4,1, 4,3, 3,4, 1,4, 0,3 } },
            ['q'] = new[] { new[] { 4,-2, 4,4 }, new[] { 4,1, 3,0, 1,0, 0,1, 0,3, 1,4, 3,4, 4,3 } },
            ['r'] = new[] { new[] { 0,0, 0,4 }, new[] { 0,3, 1,4, 3,4, 4,3 } },
            ['s'] = new[] { new[] { 4,4, 1,4, 0,3, 1,2, 3,2, 4,1, 3,0, 0,0 } },
            ['t'] = new[] { new[] { 1,6, 1,1, 2,0, 3,0 }, new[] { 0,4, 3,4 } },
            ['u'] = new[] { new[] { 0,4, 0,1, 1,0, 3,0, 4,1 }, new[] { 4,4, 4,0 } },
            ['v'] = new[] { new[] { 0,4, 2,0, 4,4 } },
            ['w'] = new[] { new[] { 0,4, 1,0, 2,2, 3,0, 4,4 } },
            ['x'] = new[] { new[] { 0,4, 4,0 }, new[] { 0,0, 4,4 } },
            ['y'] = new[] { new[] { 0,4, 2,0 }, new[] { 4,4, 0,-2 } },
            ['z'] = new[] { new[] { 0,4, 4,4, 0,0, 4,0 } },

            // ---- punctuation ----
            ['.'] = new[] { new[] { 2,0, 2,1 } },
            [','] = new[] { new[] { 2,1, 2,0, 1,-2 } },
            [':'] = new[] { new[] { 2,1, 2,2 }, new[] { 2,3, 2,4 } },
            [';'] = new[] { new[] { 2,3, 2,4 }, new[] { 2,1, 2,0, 1,-1 } },
            ['!'] = new[] { new[] { 2,6, 2,2 }, new[] { 2,1, 2,0 } },
            ['?'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4, 2,3, 2,2 }, new[] { 2,1, 2,0 } },
            ['\''] = new[] { new[] { 2,6, 2,4 } },
            ['"'] = new[] { new[] { 1,6, 1,4 }, new[] { 3,6, 3,4 } },
            ['`'] = new[] { new[] { 1,6, 2,4 } },
            ['-'] = new[] { new[] { 0,3, 4,3 } },
            ['+'] = new[] { new[] { 0,3, 4,3 }, new[] { 2,1, 2,5 } },
            ['='] = new[] { new[] { 0,2, 4,2 }, new[] { 0,4, 4,4 } },
            ['_'] = new[] { new[] { 0,0, 4,0 } },
            ['*'] = new[] { new[] { 2,2, 2,6 }, new[] { 0,3, 4,5 }, new[] { 4,3, 0,5 } },
            ['/'] = new[] { new[] { 0,0, 4,6 } },
            ['\\'] = new[] { new[] { 0,6, 4,0 } },
            ['('] = new[] { new[] { 3,6, 1,4, 1,2, 3,0 } },
            [')'] = new[] { new[] { 1,6, 3,4, 3,2, 1,0 } },
            ['['] = new[] { new[] { 3,6, 1,6, 1,0, 3,0 } },
            [']'] = new[] { new[] { 1,6, 3,6, 3,0, 1,0 } },
            ['{'] = new[] { new[] { 3,6, 2,5, 2,4, 1,3, 2,2, 2,1, 3,0 } },
            ['}'] = new[] { new[] { 1,6, 2,5, 2,4, 3,3, 2,2, 2,1, 1,0 } },
            ['<'] = new[] { new[] { 4,5, 0,3, 4,1 } },
            ['>'] = new[] { new[] { 0,5, 4,3, 0,1 } },
            ['|'] = new[] { new[] { 2,6, 2,-1 } },
            ['^'] = new[] { new[] { 0,4, 2,6, 4,4 } },
            ['~'] = new[] { new[] { 0,3, 1,4, 3,2, 4,3 } },
            ['#'] = new[] { new[] { 1,0, 1,6 }, new[] { 3,0, 3,6 }, new[] { 0,2, 4,2 }, new[] { 0,4, 4,4 } },
            ['$'] = new[] { new[] { 4,5, 3,6, 1,6, 0,5, 0,4, 1,3, 3,3, 4,2, 4,1, 3,0, 1,0, 0,1 }, new[] { 2,6, 2,0 } },
            ['%'] = new[] { new[] { 0,0, 4,6 }, new[] { 0,6, 1,6, 1,5, 0,5, 0,6 }, new[] { 3,1, 4,1, 4,0, 3,0, 3,1 } },
            ['&'] = new[] { new[] { 4,0, 1,4, 1,5, 2,6, 3,5, 0,2, 0,1, 1,0, 2,0, 4,2 } },
            ['@'] = new[] { new[] { 3,2, 2,2, 2,3, 3,3, 3,1, 4,1, 4,4, 3,5, 1,5, 0,4, 0,1, 1,0, 3,0 } },
        };
    }
}
