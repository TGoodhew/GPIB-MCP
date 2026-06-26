#!/usr/bin/env python3
"""
Generate src/Hpgl.Rendering/StrokeFont.cs from the KE5FX vector-character table.

Adopts the KE5FX glyph shapes wholesale (issue #31), mapped into our grid:
  x = col                 (KE5FX COL 0..7)
  y = 6 - row             (KE5FX ROW is Y-down with baseline at row 6; flip to our Y-up,
                           baseline = 0, cap = 6, descenders to -1)
Metrics: Em = 6 (KE5FX ink width => on-screen char width unchanged), Cap = 6,
Advance = 8.25 (= 1.375 x Em, the instrument-derived pitch from #29/#30).
"""
import os
import re
import sys

_HERE = os.path.dirname(os.path.abspath(__file__))

# Input: the KE5FX GPIB Toolkit's renderer.cpp (its default install location); override as argv[1].
RENDERER = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\KE5FX\GPIB\renderer.cpp"
# Output: src/Hpgl.Rendering/StrokeFont.cs in this repo, resolved relative to this script; override as argv[2].
OUT = sys.argv[2] if len(sys.argv) > 2 else os.path.normpath(
    os.path.join(_HERE, "..", "..", "src", "Hpgl.Rendering", "StrokeFont.cs"))
BASELINE_ROW = 6   # KE5FX row that is the text baseline

def load_tables(path):
    text = open(path, encoding="latin-1").read()
    tables = {}
    for m in re.finditer(r"u08\s+vg_([0-9A-Fa-f]{2})\s*\[\]\s*PROGMEM\s*=\s*\{(.*?)\}\s*;", text, re.S):
        tables[int(m.group(1), 16)] = [int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{2})", m.group(2))]
    return tables

def decode(byts):
    if not byts or byts[0] == 0xFF:
        return []
    strokes, cur, prev = [], [], None
    for b in byts:
        if b == 0xFF:
            break
        if not (b & 0x80):                 # pen-up move -> new stroke
            if cur:
                strokes.append(cur)
            cur = [((b >> 4) & 7, b & 7)]
        else:                              # line from previous point
            if not cur:
                cur = [prev] if prev else []
            cur.append(((b >> 4) & 7, b & 7))
        prev = ((b >> 4) & 7, b & 7)
        if b & 0x08:
            break
    if cur:
        strokes.append(cur)
    return strokes

def to_grid(strokes):
    """Map KE5FX (col,row) -> our (x, 6-row); make a lone point a tiny dot segment."""
    out = []
    for s in strokes:
        pts = [(c, BASELINE_ROW - r) for (c, r) in s]
        if len(pts) == 1:                  # dot: render as a 1-unit vertical so it shows
            x, y = pts[0]
            pts = [(x, y), (x, y - 1)]
        out.append(pts)
    return out

def key_literal(code):
    ch = chr(code)
    if ch == "'": return r"'\''"
    if ch == "\\": return r"'\\'"
    return f"'{ch}'"

def stroke_literal(pts):
    return "new[] { " + ", ".join(f"{x},{y}" for (x, y) in pts) + " }"

def glyph_line(code, strokes):
    key = key_literal(code)
    if not strokes:
        return f"            [{key}] = new int[0][],"
    body = ", ".join(stroke_literal(s) for s in strokes)
    return f"            [{key}] = new[] {{ {body} }},"

HEADER = '''// -----------------------------------------------------------------------------
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

        /// <summary>Returns the glyph as a list of pen-down polylines (grid units), or null if undrawn.</summary>
        public static int[][] Get(char c)
        {
            int[][] g;
            return Glyphs.TryGetValue(c, out g) ? g : null;
        }

        // Each entry: an array of strokes; each stroke is a flat {x0,y0,x1,y1,...} polyline.
        private static readonly Dictionary<char, int[][]> Glyphs = new Dictionary<char, int[][]>
        {
'''

FOOTER = '''        };
    }
}
'''

def main():
    tables = load_tables(RENDERER)
    lines = []
    maxcol = 0
    for code in range(0x20, 0x7F):
        strokes = to_grid(decode(tables.get(code, [0xFF])))
        for s in strokes:
            for (x, y) in s:
                maxcol = max(maxcol, x)
        lines.append(glyph_line(code, strokes))
    open(OUT, "w", encoding="utf-8").write(HEADER + "\n".join(lines) + "\n" + FOOTER)
    print(f"wrote {OUT}")
    print(f"glyphs: {len(lines)} (0x20..0x7E), max col used = {maxcol}")

if __name__ == "__main__":
    main()
