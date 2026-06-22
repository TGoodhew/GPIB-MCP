#!/usr/bin/env python3
"""
Extract the KE5FX vector-character font from renderer.cpp and decode each printable
ASCII glyph into single-stroke polylines.

KE5FX byte encoding (renderer.cpp ~line 700):
  bit 0x80  LINE  : 1 = draw a line from the previous point to this point; 0 = pen-up move
  bits 0x70 COL   : X, 0..7   (>>4 & 7)
  bit 0x08  EOL   : last byte in this glyph's stroke list
  bits 0x07 ROW   : Y, 0..7   (& 7)   (Y up; ROW 0 = bottom)
  0xFF            : null character
vgen[c] maps char code c -> vg_<hex(c)>.  Grid is 8x8 (VCHAR_W = VCHAR_H = 8).
"""
import re, sys

RENDERER = r"C:\Program Files (x86)\KE5FX\GPIB\renderer.cpp"

def load_tables(path):
    text = open(path, encoding="latin-1").read()
    tables = {}
    for m in re.finditer(r"u08\s+vg_([0-9A-Fa-f]{2})\s*\[\]\s*PROGMEM\s*=\s*\{(.*?)\}\s*;", text, re.S):
        code = int(m.group(1), 16)
        body = m.group(2)
        bytes_ = [int(x, 16) for x in re.findall(r"0x([0-9A-Fa-f]{2})", body)]
        tables[code] = bytes_
    return tables

def decode(byts):
    """Return list of strokes; each stroke is a list of (x,y) points in the 0..7 grid."""
    if not byts or byts[0] == 0xFF:
        return []
    strokes, cur, prev = [], [], None
    for b in byts:
        if b == 0xFF:
            break
        line = b & 0x80
        col = (b >> 4) & 0x07
        row = b & 0x07
        eol = b & 0x08
        if not line:                       # pen-up move -> start a new stroke
            if len(cur) >= 1:
                strokes.append(cur)
            cur = [(col, row)]
        else:                              # line from previous point to here
            if not cur:
                cur = [prev] if prev else [(col, row)]
            cur.append((col, row))
        prev = (col, row)
        if eol:
            break
    if cur:
        strokes.append(cur)
    return strokes

def extent(strokes):
    pts = [p for s in strokes for p in s]
    if not pts:
        return None
    xs = [p[0] for p in pts]; ys = [p[1] for p in pts]
    return min(xs), max(xs), min(ys), max(ys)

def main():
    tables = load_tables(RENDERER)
    print("vg tables parsed:", len(tables))

    # Metric analysis over printable ASCII.
    cats = {
        "digits 0-9": range(0x30, 0x3A),
        "upper A-Z":  range(0x41, 0x5B),
        "lower a-z":  range(0x61, 0x7B),
    }
    for name, rng in cats.items():
        xmin=ymin=99; xmax=ymax=-99
        for c in rng:
            e = extent(decode(tables.get(c, [0xFF])))
            if not e: continue
            xmin=min(xmin,e[0]); xmax=max(xmax,e[1]); ymin=min(ymin,e[2]); ymax=max(ymax,e[3])
        print(f"  {name:12s}  X {xmin}..{xmax}   Y {ymin}..{ymax}")

    # Descender check: lowercase that dip lowest.
    print("lowercase Y-min (descenders):")
    for ch in "gjpqy":
        e = extent(decode(tables.get(ord(ch), [0xFF])))
        print(f"   '{ch}'  Y {e[2]}..{e[3]}" if e else f"   '{ch}' (none)")

    # Sample decodes to eyeball vs known shapes.
    for ch in "A0g.":
        print(f"glyph '{ch}' (0x{ord(ch):02X}):")
        for s in decode(tables.get(ord(ch), [0xFF])):
            print("   ", s)
    print("space 0x20:", decode(tables.get(0x20, [0xFF])))

if __name__ == "__main__":
    main()
