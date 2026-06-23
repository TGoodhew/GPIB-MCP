// -----------------------------------------------------------------------------
// Hpgl.Rendering - single-stroke vector font for HP-GL labels (Set 0 / ASCII).
//
// GENERATED from the KE5FX HP7470A Plotter Emulator vector-character table
// (renderer.cpp `vgen[]` / `vg_xx[]`, by John Miles + Mark S. Sims, http://www.ke5fx.com/)
// by tools/ke5fx_font/generate_strokefont.py. The HP plotters' own glyph outlines are
// not published as coordinate tables; KE5FX's table is the reproducible single-stroke
// reference this project renders against (issue #31). Do not hand-edit - regenerate.
//
// Mapping from the KE5FX 8x8 grid: x = COL (0..7); y = 6 - ROW (KE5FX ROW is Y-down with
// the baseline at row 6, flipped to our Y-up baseline = 0, cap = 6, descenders to -1).
// Each glyph is one or more pen-down polylines, so the renderer draws real strokes that
// honour size, slant, direction, rotation, clipping, pen colour and line type uniformly.
// -----------------------------------------------------------------------------

using System.Collections.Generic;

namespace Hpgl.Rendering
{
    /// <summary>A single-stroke font for HP-GL Set 0 (ASCII) labels, from the KE5FX vector-char table.</summary>
    internal static class StrokeFont
    {
        /// <summary>Capital height in grid units (maps to the current character height).</summary>
        public const int Cap = 6;

        /// <summary>Grid units that map to one HP-GL character width (SI/SR). KE5FX glyph ink spans 0..6,
        /// so the on-screen character width is unchanged from the prior hand-drawn 0..4 / Em=4 font.</summary>
        public const double Em = 6.0;

        /// <summary>Cell advance in grid units. The fixed monospaced pitch is Advance/Em (= 1.375x) the
        /// character width - the instrument-derived character-cell grid from #29/#30.</summary>
        public const double Advance = 8.25;

        /// <summary>
        /// Returns the glyph for character <paramref name="c"/> in HP-GL character set <paramref name="set"/>
        /// as a list of pen-down polylines (grid units), or null if undrawn. Only Set 0 (ASCII) is
        /// implemented; any other set currently falls back to the Set-0 glyph (see <see cref="IsImplemented"/>
        /// - the typography analyzer flags those so a capture self-reports the gap, #56).
        /// </summary>
        public static int[][] Get(char c, int set = 0)
        {
            int[][] g;
            return Glyphs.TryGetValue(c, out g) ? g : null;   // set is ignored until non-ASCII sets land (#56)
        }

        /// <summary>True if a glyph table exists for this HP-GL character set. Only Set 0 (ASCII) so far.</summary>
        public static bool IsImplemented(int set) => set == 0;

        // Each entry: an array of strokes; each stroke is a flat {x0,y0,x1,y1,...} polyline.
        private static readonly Dictionary<char, int[][]> Glyphs = new Dictionary<char, int[][]>
        {
            [' '] = new int[0][],
            ['!'] = new[] { new[] { 1,5, 2,6, 3,5, 3,3 }, new[] { 1,5, 3,3 }, new[] { 1,5, 3,5 }, new[] { 1,5, 1,3, 2,2, 3,3 }, new[] { 1,4, 3,4 }, new[] { 1,3, 3,5 }, new[] { 1,3, 3,3 }, new[] { 2,6, 2,2 }, new[] { 2,0, 2,-1 } },
            ['"'] = new[] { new[] { 1,6, 1,4 }, new[] { 4,6, 4,4 } },
            ['#'] = new[] { new[] { 0,4, 5,4 }, new[] { 0,2, 5,2 }, new[] { 1,6, 1,0 }, new[] { 4,6, 4,0 } },
            ['$'] = new[] { new[] { 0,4, 1,3, 3,3, 4,2 }, new[] { 0,4, 1,5, 4,5 }, new[] { 0,1, 3,1, 4,2 }, new[] { 2,6, 2,5 }, new[] { 2,1, 2,0 } },
            ['%'] = new[] { new[] { 0,5, 1,5, 1,4 }, new[] { 0,5, 0,4, 1,4 }, new[] { 0,0, 5,5 }, new[] { 4,1, 5,1, 5,0 }, new[] { 4,1, 4,0, 5,0 } },
            ['&'] = new[] { new[] { 0,2, 2,4, 3,4, 5,2, 6,3 }, new[] { 0,2, 0,1, 1,0, 4,0, 5,1, 6,0 }, new[] { 1,5, 2,4 }, new[] { 1,5, 2,6, 3,6, 4,5 }, new[] { 3,4, 4,5 }, new[] { 5,2, 5,1 } },
            ['\''] = new[] { new[] { 0,4, 1,5 }, new[] { 1,6, 1,5 } },
            ['('] = new[] { new[] { 1,4, 3,6 }, new[] { 1,4, 1,2, 3,0 } },
            [')'] = new[] { new[] { 1,6, 3,4, 3,2 }, new[] { 1,0, 3,2 } },
            ['*'] = new[] { new[] { 0,3, 6,3 }, new[] { 1,5, 5,1 }, new[] { 1,1, 5,5 }, new[] { 3,6, 3,0 } },
            ['+'] = new[] { new[] { 0,3, 4,3 }, new[] { 2,5, 2,1 } },
            [','] = new[] { new[] { 1,-1, 2,0 }, new[] { 2,1, 2,0 } },
            ['-'] = new[] { new[] { 0,3, 4,3 } },
            ['.'] = new[] { new[] { 2,1, 2,0 } },
            ['/'] = new[] { new[] { 0,1, 5,6 } },
            ['0'] = new[] { new[] { 0,5, 1,6, 4,6, 5,5, 5,1 }, new[] { 0,5, 0,1, 1,0, 4,0, 5,1 }, new[] { 0,1, 5,5 } },
            ['1'] = new[] { new[] { 1,5, 2,6, 2,0 }, new[] { 1,0, 3,0 } },
            ['2'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4 }, new[] { 0,1, 2,3, 3,3, 4,4 }, new[] { 0,1, 0,0, 4,0 } },
            ['3'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4 }, new[] { 0,1, 1,0, 3,0, 4,1 }, new[] { 2,3, 3,3, 4,2, 4,1 }, new[] { 3,3, 4,4 } },
            ['4'] = new[] { new[] { 0,3, 3,6, 4,6, 4,0 }, new[] { 0,3, 0,2, 5,2 } },
            ['5'] = new[] { new[] { 0,6, 4,6 }, new[] { 0,6, 0,4, 3,4, 4,3, 4,1 }, new[] { 0,1, 1,0, 3,0, 4,1 } },
            ['6'] = new[] { new[] { 0,4, 2,6, 3,6 }, new[] { 0,4, 0,1, 1,0, 3,0, 4,1 }, new[] { 0,3, 3,3, 4,2, 4,1 } },
            ['7'] = new[] { new[] { 0,6, 4,6, 4,4 }, new[] { 0,6, 0,5 }, new[] { 2,2, 4,4 }, new[] { 2,2, 2,0 } },
            ['8'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4 }, new[] { 0,5, 0,4, 1,3, 3,3, 4,2, 4,1 }, new[] { 0,2, 1,3 }, new[] { 0,2, 0,1, 1,0, 3,0, 4,1 }, new[] { 3,3, 4,4 } },
            ['9'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,2 }, new[] { 0,5, 0,4, 1,3, 4,3 }, new[] { 1,0, 2,0, 4,2 } },
            [':'] = new[] { new[] { 2,5, 2,4 }, new[] { 2,1, 2,0 } },
            [';'] = new[] { new[] { 1,-1, 2,0 }, new[] { 2,5, 2,4 }, new[] { 2,1, 2,0 } },
            ['<'] = new[] { new[] { 0,3, 3,0 }, new[] { 0,3, 3,6 } },
            ['='] = new[] { new[] { 0,4, 4,4 }, new[] { 0,1, 4,1 } },
            ['>'] = new[] { new[] { 1,6, 4,3 }, new[] { 1,0, 4,3 } },
            ['?'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,4 }, new[] { 2,2, 4,4 }, new[] { 2,2, 2,1 }, new[] { 2,-1, 2,-2 } },
            ['@'] = new[] { new[] { 0,5, 1,6, 4,6, 5,5, 5,2 }, new[] { 0,5, 0,1, 1,0, 3,0 }, new[] { 3,4, 3,2, 5,2 } },
            ['A'] = new[] { new[] { 0,4, 2,6, 4,4, 4,0 }, new[] { 0,4, 0,0 }, new[] { 0,2, 4,2 } },
            ['B'] = new[] { new[] { 0,6, 3,6, 4,5, 4,4 }, new[] { 0,6, 0,0, 3,0, 4,1 }, new[] { 0,3, 3,3, 4,2, 4,1 }, new[] { 3,3, 4,4 } },
            ['C'] = new[] { new[] { 0,4, 2,6, 4,6, 5,5 }, new[] { 0,4, 0,2, 2,0, 4,0, 5,1 } },
            ['D'] = new[] { new[] { 0,6, 2,6, 4,4, 4,2 }, new[] { 0,6, 0,0, 2,0, 4,2 } },
            ['E'] = new[] { new[] { 1,6, 5,6 }, new[] { 1,6, 1,0, 5,0 }, new[] { 1,3, 3,3 } },
            ['F'] = new[] { new[] { 1,6, 5,6 }, new[] { 1,6, 1,0 }, new[] { 1,3, 3,3 } },
            ['G'] = new[] { new[] { 0,4, 2,6, 4,6, 5,5 }, new[] { 0,4, 0,2, 2,0, 5,0 }, new[] { 4,2, 5,2, 5,0 } },
            ['H'] = new[] { new[] { 0,6, 0,0 }, new[] { 0,3, 4,3 }, new[] { 4,6, 4,0 } },
            ['I'] = new[] { new[] { 1,6, 3,6 }, new[] { 1,0, 3,0 }, new[] { 2,6, 2,0 } },
            ['J'] = new[] { new[] { 0,2, 0,1, 1,0, 3,0, 4,1 }, new[] { 3,6, 5,6 }, new[] { 4,6, 4,1 } },
            ['K'] = new[] { new[] { 0,6, 0,0 }, new[] { 0,3, 2,3, 4,1, 4,0 }, new[] { 2,3, 4,5 }, new[] { 4,6, 4,5 } },
            ['L'] = new[] { new[] { 1,6, 1,0, 5,0 } },
            ['M'] = new[] { new[] { 0,6, 3,3, 6,6, 6,0 }, new[] { 0,6, 0,0 }, new[] { 3,3, 3,2 } },
            ['N'] = new[] { new[] { 0,6, 5,1 }, new[] { 0,6, 0,0 }, new[] { 5,6, 5,0 } },
            ['O'] = new[] { new[] { 0,4, 2,6, 3,6, 5,4, 5,2 }, new[] { 0,4, 0,2, 2,0, 3,0, 5,2 } },
            ['P'] = new[] { new[] { 1,6, 4,6, 5,5, 5,4 }, new[] { 1,6, 1,0 }, new[] { 1,3, 4,3, 5,4 } },
            ['Q'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5, 4,1 }, new[] { 0,5, 0,1, 1,0, 3,0, 4,1 }, new[] { 2,2, 5,-1 } },
            ['R'] = new[] { new[] { 1,6, 4,6, 5,5, 5,4 }, new[] { 1,6, 1,0 }, new[] { 1,3, 4,3, 5,4 }, new[] { 3,3, 5,1, 5,0 } },
            ['S'] = new[] { new[] { 0,5, 1,6, 3,6, 4,5 }, new[] { 0,5, 0,4, 1,3, 3,3, 4,2, 4,1 }, new[] { 0,1, 1,0, 3,0, 4,1 } },
            ['T'] = new[] { new[] { 0,6, 4,6 }, new[] { 2,6, 2,0 } },
            ['U'] = new[] { new[] { 0,6, 0,1, 1,0, 3,0, 4,1 }, new[] { 4,6, 4,1 } },
            ['V'] = new[] { new[] { 0,6, 0,2, 2,0, 4,2 }, new[] { 4,6, 4,2 } },
            ['W'] = new[] { new[] { 0,6, 0,0, 3,3, 6,0 }, new[] { 3,4, 3,3 }, new[] { 6,6, 6,0 } },
            ['X'] = new[] { new[] { 0,6, 0,5, 5,0 }, new[] { 0,0, 5,5 }, new[] { 5,6, 5,5 } },
            ['Y'] = new[] { new[] { 0,6, 0,4, 2,2, 4,4 }, new[] { 1,0, 3,0 }, new[] { 2,2, 2,0 }, new[] { 4,6, 4,4 } },
            ['Z'] = new[] { new[] { 1,6, 5,6, 5,5 }, new[] { 1,1, 5,5 }, new[] { 1,1, 1,0, 5,0 } },
            ['['] = new[] { new[] { 1,6, 3,6 }, new[] { 1,6, 1,0, 3,0 } },
            ['\\'] = new[] { new[] { 0,6, 6,0 } },
            [']'] = new[] { new[] { 1,6, 3,6, 3,0 }, new[] { 1,0, 3,0 } },
            ['^'] = new[] { new[] { 0,3, 3,6, 6,3 } },
            ['_'] = new[] { new[] { 0,-1, 6,-1 } },
            ['`'] = new[] { new[] { 2,6, 2,5, 3,4 } },
            ['a'] = new[] { new[] { 0,1, 1,0, 3,0, 4,1, 5,0 }, new[] { 0,1, 1,2, 4,2 }, new[] { 1,4, 3,4, 4,3, 4,1 } },
            ['b'] = new[] { new[] { 0,0, 1,1, 2,0, 4,0, 5,1 }, new[] { 1,6, 1,1 }, new[] { 1,3, 4,3, 5,2, 5,1 } },
            ['c'] = new[] { new[] { 0,3, 1,4, 3,4, 4,3 }, new[] { 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['d'] = new[] { new[] { 0,2, 1,3, 4,3 }, new[] { 0,2, 0,1, 1,0, 3,0, 4,1, 5,0 }, new[] { 4,6, 4,1 } },
            ['e'] = new[] { new[] { 0,3, 1,4, 3,4, 4,3, 4,2 }, new[] { 0,3, 0,1, 1,0, 3,0 }, new[] { 0,2, 4,2 } },
            ['f'] = new[] { new[] { 0,3, 2,3 }, new[] { 0,0, 2,0 }, new[] { 1,5, 2,6, 3,6, 4,5 }, new[] { 1,5, 1,0 } },
            ['g'] = new[] { new[] { 0,3, 1,4, 3,4, 4,3, 5,4 }, new[] { 0,3, 0,2, 1,1, 4,1 }, new[] { 0,-1, 3,-1, 4,0 }, new[] { 4,3, 4,0 } },
            ['h'] = new[] { new[] { 1,6, 1,0 }, new[] { 1,2, 3,4, 4,4, 5,3, 5,0 } },
            ['i'] = new[] { new[] { 1,4, 2,4, 2,0 }, new[] { 1,0, 3,0 }, new[] { 2,6, 2,5 } },
            ['j'] = new[] { new[] { 0,1, 0,0, 1,-1, 3,-1, 4,0 }, new[] { 4,6, 4,5 }, new[] { 4,4, 4,0 } },
            ['k'] = new[] { new[] { 1,6, 1,0 }, new[] { 1,2, 3,2, 5,0 }, new[] { 3,2, 5,4 } },
            ['l'] = new[] { new[] { 1,6, 2,6, 2,0 }, new[] { 1,0, 3,0 } },
            ['m'] = new[] { new[] { 0,4, 0,0 }, new[] { 0,2, 2,4, 3,3, 4,4, 5,3, 5,0 }, new[] { 3,3, 3,1 } },
            ['n'] = new[] { new[] { 0,4, 0,0 }, new[] { 0,2, 2,4, 3,4, 4,3, 4,0 } },
            ['o'] = new[] { new[] { 0,3, 1,4, 3,4, 4,3, 4,1 }, new[] { 0,3, 0,1, 1,0, 3,0, 4,1 } },
            ['p'] = new[] { new[] { 0,4, 1,3, 2,4, 4,4, 5,3, 5,2 }, new[] { 0,-1, 2,-1 }, new[] { 1,3, 1,-1 }, new[] { 1,1, 4,1, 5,2 } },
            ['q'] = new[] { new[] { 0,3, 1,4, 3,4, 4,3, 5,4 }, new[] { 0,3, 0,2, 1,1, 4,1 }, new[] { 3,-1, 5,-1 }, new[] { 4,3, 4,-1 } },
            ['r'] = new[] { new[] { 0,4, 1,3, 2,4, 3,4, 4,3 }, new[] { 0,0, 2,0 }, new[] { 1,3, 1,0 } },
            ['s'] = new[] { new[] { 0,3, 1,2, 3,2, 4,1 }, new[] { 0,3, 1,4, 4,4 }, new[] { 0,0, 3,0, 4,1 } },
            ['t'] = new[] { new[] { 1,4, 4,4 }, new[] { 2,6, 2,1, 3,0, 4,1 } },
            ['u'] = new[] { new[] { 0,4, 0,1, 1,0, 3,0, 4,1, 5,0 }, new[] { 4,4, 4,1 } },
            ['v'] = new[] { new[] { 0,4, 0,2, 2,0, 4,2 }, new[] { 4,4, 4,2 } },
            ['w'] = new[] { new[] { 0,4, 0,1, 1,0, 3,2, 5,0, 6,1 }, new[] { 3,3, 3,2 }, new[] { 6,4, 6,1 } },
            ['x'] = new[] { new[] { 0,4, 2,2, 3,2, 5,0 }, new[] { 0,0, 2,2 }, new[] { 3,2, 5,4 } },
            ['y'] = new[] { new[] { 0,4, 0,2, 1,1, 4,1 }, new[] { 0,-1, 3,-1, 4,0 }, new[] { 4,4, 4,0 } },
            ['z'] = new[] { new[] { 0,4, 4,4 }, new[] { 0,0, 4,4 }, new[] { 0,0, 4,0 } },
            ['{'] = new[] { new[] { 0,3, 1,3, 2,2, 2,1, 3,0, 4,0 }, new[] { 1,3, 2,4 }, new[] { 2,5, 3,6, 4,6 }, new[] { 2,5, 2,4 } },
            ['|'] = new[] { new[] { 3,6, 3,4 }, new[] { 3,2, 3,0 } },
            ['}'] = new[] { new[] { 0,6, 1,6, 2,5, 2,4, 3,3, 4,3 }, new[] { 0,0, 1,0, 2,1 }, new[] { 2,2, 3,3 }, new[] { 2,2, 2,1 } },
            ['~'] = new[] { new[] { 0,5, 1,6, 2,6, 3,5, 4,5, 5,6 } },
        };
    }
}
