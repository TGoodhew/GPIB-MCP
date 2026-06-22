// -----------------------------------------------------------------------------
// Hpgl.Rendering - HP PCL raster-graphics decoder (.NET Framework 4.7.2).
//
// Decodes an HP PCL (Printer Command Language) byte stream - the "print" hardcopy
// many GPIB instruments emit as an alternative to an HP-GL "plot" - into a
// monochrome raster image. Implements the PCL 5 raster-graphics command set and
// all four compression methods plus adaptive compression, per HP's "PCL 5 Printer
// Language Technical Reference Manual", Chapter 15 (Raster Graphics).
//
// Scope: what instrument screen/print dumps actually use - the raster pipeline
// (resolution / presentation / width / height / start / compression / transfer /
// end) and a hand-off marker for embedded HP-GL/2 picture blocks. PCL font/text
// typography is out of scope (see issue #40).
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>A decoded monochrome PCL raster: one bit per dot, MSB-first within each byte.</summary>
    internal sealed class PclRasterImage
    {
        /// <summary>Image width in dots (pixels).</summary>
        public int Width { get; }

        /// <summary>Image height in dots (raster rows).</summary>
        public int Height { get; }

        /// <summary>Packed rows; each is <see cref="RowBytes"/> long, bit 7 of byte 0 = leftmost dot.</summary>
        public IReadOnlyList<byte[]> Rows { get; }

        /// <summary>Bytes per row (ceil(Width / 8)).</summary>
        public int RowBytes { get; }

        /// <summary>Raster graphics resolution in dots-per-inch (PCL default 75).</summary>
        public int ResolutionDpi { get; }

        /// <summary>HP-GL/2 picture content found in embedded <c>ESC%#B</c> blocks (null when none).</summary>
        public string EmbeddedHpgl { get; }

        public PclRasterImage(int width, int height, IReadOnlyList<byte[]> rows, int rowBytes,
                              int resolutionDpi, string embeddedHpgl)
        {
            Width = width;
            Height = height;
            Rows = rows;
            RowBytes = rowBytes;
            ResolutionDpi = resolutionDpi;
            EmbeddedHpgl = embeddedHpgl;
        }

        /// <summary>True when the raster carries no dot rows (e.g. an HP-GL/2-only print job).</summary>
        public bool IsEmpty => Height == 0 || Width == 0;
    }

    /// <summary>
    /// Parses a PCL byte stream and reconstructs the raster image. The parser follows the PCL
    /// escape grammar (parameterized + combined sequences and two-character escapes) and decodes
    /// every Transfer-Raster-Data (<c>ESC*b#W</c>) row against the current compression method,
    /// maintaining the delta-row seed row across transfers exactly as the printer does.
    /// </summary>
    internal static class PclRasterDecoder
    {
        public static PclRasterImage Decode(byte[] data)
        {
            if (data == null) data = Array.Empty<byte>();
            var rows = new List<byte[]>();
            byte[] seed = Array.Empty<byte>();      // delta-row seed; grows as rows widen
            int compression = 0;                    // ESC*b#M
            int rasterWidth = 0;                    // ESC*r#S (0 = unset/infer)
            int rasterHeight = 0;                   // ESC*r#T (0 = unset)
            int resolution = 75;                    // ESC*t#R
            StringBuilder hpgl = null;              // embedded HP-GL/2 (ESC%#B .. ESC%#A)

            int p = 0, n = data.Length;
            while (p < n)
            {
                if (data[p] != 0x1B) { p++; continue; }   // skip stray text/whitespace between escapes
                p++;                                       // consume ESC
                if (p >= n) break;

                byte c = data[p];
                if (c >= 33 && c <= 47)
                {
                    // Parameterized escape: ESC <param 33-47> [group 96-126] {value <term>} ...
                    p++;
                    byte group = 0;
                    if (p < n && data[p] >= 96 && data[p] <= 126) { group = data[p]; p++; }

                    while (p < n)
                    {
                        int value = ReadValue(data, ref p, out bool hadValue);
                        if (p >= n) break;
                        byte term = data[p++];
                        bool terminates = term >= 64 && term <= 94;          // uppercase ends the sequence
                        char cmd = terminates ? (char)term : (char)(term - 32); // lowercase = combined param

                        ApplyCommand((char)c, (char)group, cmd, value, hadValue, data, ref p,
                                     rows, ref seed, ref compression, ref rasterWidth, ref rasterHeight,
                                     ref resolution, ref hpgl);

                        if (terminates) break;
                    }
                }
                else
                {
                    // Two-character escape, e.g. ESC E (printer reset).
                    p++;
                    if (c == 'E') { seed = Array.Empty<byte>(); compression = 0; } // reset: keep captured rows
                }
            }

            return Build(rows, rasterWidth, rasterHeight, resolution, hpgl);
        }

        /// <summary>Reads an optional signed integer value field, leaving <paramref name="p"/> on the terminator.</summary>
        private static int ReadValue(byte[] d, ref int p, out bool hadValue)
        {
            int n = d.Length;
            bool neg = false;
            if (p < n && (d[p] == '+' || d[p] == '-')) { neg = d[p] == '-'; p++; }
            long v = 0; hadValue = false;
            while (p < n && d[p] >= '0' && d[p] <= '9') { v = v * 10 + (d[p] - '0'); p++; hadValue = true; }
            if (p < n && d[p] == '.') { p++; while (p < n && d[p] >= '0' && d[p] <= '9') p++; } // discard fraction
            return (int)(neg ? -v : v);
        }

        private static void ApplyCommand(char param, char group, char cmd, int value, bool hadValue,
            byte[] data, ref int p, List<byte[]> rows, ref byte[] seed, ref int compression,
            ref int rasterWidth, ref int rasterHeight, ref int resolution, ref StringBuilder hpgl)
        {
            if (param == '*')
            {
                if (group == 't' && cmd == 'R') { resolution = value; return; }
                if (group == 'r')
                {
                    switch (cmd)
                    {
                        case 'S': rasterWidth = value; return;                 // raster width (pixels)
                        case 'T': rasterHeight = value; return;                // raster height (rows)
                        case 'A': seed = Array.Empty<byte>(); return;          // start raster -> zero seed
                        case 'B': case 'C': seed = Array.Empty<byte>(); return; // end raster -> reset seed
                        default: return;                                       // F (presentation), U (planes): ignore (mono)
                    }
                }
                if (group == 'b')
                {
                    switch (cmd)
                    {
                        case 'M': compression = value; return;                 // set compression method
                        case 'W': TransferRow(data, ref p, value, compression, rows, ref seed); return;
                        case 'Y': YOffset(value, rows, ref seed); return;       // vertical move = zero rows
                        default: return;
                    }
                }
                return;
            }

            if (param == '%')
            {
                // ESC%#B = enter HP-GL/2; capture the picture bytes until the matching ESC%#A / ESC E.
                if (cmd == 'B') { hpgl = hpgl ?? new StringBuilder(); CaptureHpgl(data, ref p, hpgl); }
                return;
            }
        }

        /// <summary>ESC*b#W: read <paramref name="count"/> payload bytes, decode them to a row, append, update the seed.</summary>
        private static void TransferRow(byte[] data, ref int p, int count, int compression,
                                        List<byte[]> rows, ref byte[] seed)
        {
            if (count < 0) count = 0;
            int avail = Math.Min(count, data.Length - p);
            var payload = new byte[avail];
            Array.Copy(data, p, payload, 0, avail);
            p += avail;

            switch (compression)
            {
                case 1: AppendRow(rows, ref seed, DecodeRunLength(payload)); break;
                case 2: AppendRow(rows, ref seed, DecodeTiff(payload)); break;
                case 3: AppendRow(rows, ref seed, DecodeDelta(payload, seed)); break;
                case 5: DecodeAdaptive(payload, rows, ref seed); break;
                default:                                                // 0 (unencoded) and unknown
                    if (count == 0) AppendRow(rows, ref seed, (byte[])seed.Clone()); // repeat previous row
                    else AppendRow(rows, ref seed, payload);
                    break;
            }
        }

        /// <summary>ESC*b#Y: move down <paramref name="value"/> raster rows, emitting zeroed rows and zeroing the seed.</summary>
        private static void YOffset(int value, List<byte[]> rows, ref byte[] seed)
        {
            int width = seed.Length;
            for (int i = 0; i < value && i < 32767; i++) rows.Add(new byte[width]);
            seed = new byte[width];
        }

        private static void AppendRow(List<byte[]> rows, ref byte[] seed, byte[] row)
        {
            rows.Add(row);
            seed = row;
        }

        // ---- Compression methods (Tech Ref ch.15) ---------------------------

        /// <summary>Method 1: pairs of (repetition-count, pattern-byte); count 0 = 1 occurrence.</summary>
        private static byte[] DecodeRunLength(byte[] src)
        {
            var outp = new List<byte>(src.Length * 2);
            for (int i = 0; i + 1 < src.Length; i += 2)
            {
                int reps = src[i] + 1;
                byte val = src[i + 1];
                for (int k = 0; k < reps; k++) outp.Add(val);
            }
            return outp.ToArray();
        }

        /// <summary>Method 2: TIFF PackBits - control byte, then literal run (0..127) or repeat (-1..-127); -128 = NOP.</summary>
        private static byte[] DecodeTiff(byte[] src)
        {
            var outp = new List<byte>(src.Length * 2);
            int i = 0;
            while (i < src.Length)
            {
                sbyte ctrl = unchecked((sbyte)src[i++]);
                if (ctrl >= 0)
                {
                    int lit = ctrl + 1;
                    for (int k = 0; k < lit && i < src.Length; k++) outp.Add(src[i++]);
                }
                else if (ctrl != -128)
                {
                    int reps = -ctrl + 1;
                    if (i < src.Length) { byte v = src[i++]; for (int k = 0; k < reps; k++) outp.Add(v); }
                }
                // ctrl == -128: no-op, next byte is a fresh control byte
            }
            return outp.ToArray();
        }

        /// <summary>
        /// Method 3: delta row - a copy of the seed row patched by [command byte][replacement bytes] groups.
        /// Command byte = (replace-count-1)&lt;&lt;5 | offset; offset 31 extends via following bytes (sum until &lt;255).
        /// </summary>
        private static byte[] DecodeDelta(byte[] src, byte[] seed)
        {
            var row = new List<byte>(seed);
            int pos = 0, i = 0;
            while (i < src.Length)
            {
                byte command = src[i++];
                int replace = ((command >> 5) & 0x07) + 1;
                int offset = command & 0x1F;
                if (offset == 31)
                    while (i < src.Length) { byte ob = src[i++]; offset += ob; if (ob != 255) break; }

                pos += offset;
                for (int k = 0; k < replace && i < src.Length; k++)
                {
                    while (row.Count <= pos) row.Add(0);
                    row[pos++] = src[i++];
                }
            }
            return row.ToArray();
        }

        /// <summary>
        /// Method 5: adaptive - a block of rows, each prefixed by [op][count-hi][count-lo]. Ops 0-3 select a
        /// per-row method (count = row byte length), 4 = N empty rows, 5 = N duplicate rows.
        /// </summary>
        private static void DecodeAdaptive(byte[] block, List<byte[]> rows, ref byte[] seed)
        {
            int i = 0;
            while (i + 2 < block.Length)
            {
                byte op = block[i++];
                int cnt = (block[i++] << 8) | block[i++];
                if (op <= 3)
                {
                    int take = Math.Min(cnt, block.Length - i);
                    var sub = new byte[take];
                    Array.Copy(block, i, sub, 0, take);
                    i += take;
                    byte[] row;
                    switch (op)
                    {
                        case 1: row = DecodeRunLength(sub); break;
                        case 2: row = DecodeTiff(sub); break;
                        case 3: row = DecodeDelta(sub, seed); break;
                        default: row = sub; break;
                    }
                    AppendRow(rows, ref seed, row);
                }
                else if (op == 4) { for (int r = 0; r < cnt; r++) rows.Add(new byte[seed.Length]); seed = new byte[seed.Length]; }
                else if (op == 5) { for (int r = 0; r < cnt; r++) rows.Add((byte[])seed.Clone()); }
                else break; // out-of-range op: skip the rest of the block
            }
        }

        /// <summary>Captures the bytes of an embedded HP-GL/2 block (ESC%#B) up to the next ESC%#A / ESC E.</summary>
        private static void CaptureHpgl(byte[] data, ref int p, StringBuilder hpgl)
        {
            int n = data.Length;
            while (p < n)
            {
                if (data[p] == 0x1B)
                {
                    // ESC % ... A  (back to PCL)  or  ESC E (reset) ends the HP-GL/2 block.
                    if (p + 1 < n && data[p + 1] == '%') return;       // leave ESC for the main parser
                    if (p + 1 < n && data[p + 1] == 'E') return;
                }
                hpgl.Append((char)data[p]);
                p++;
            }
        }

        // ---- Assemble the final image ---------------------------------------

        private static PclRasterImage Build(List<byte[]> rows, int rasterWidth, int rasterHeight,
                                            int resolution, StringBuilder hpgl)
        {
            int maxRowBytes = 0;
            foreach (var r in rows) if (r.Length > maxRowBytes) maxRowBytes = r.Length;

            int rowBytes = rasterWidth > 0 ? (rasterWidth + 7) / 8 : maxRowBytes;
            if (rowBytes < maxRowBytes) rowBytes = maxRowBytes; // never truncate captured data
            int width = rasterWidth > 0 ? rasterWidth : rowBytes * 8;

            // Normalize every row to rowBytes (zero-pad short rows from the compression decoders).
            var packed = new List<byte[]>(rows.Count);
            foreach (var r in rows)
            {
                if (r.Length == rowBytes) { packed.Add(r); continue; }
                var fixedRow = new byte[rowBytes];
                Array.Copy(r, fixedRow, Math.Min(r.Length, rowBytes));
                packed.Add(fixedRow);
            }

            // Honour an explicit raster height as a hint: pad with zero rows, but never trust a wildly
            // out-of-range value (the spec clips to the page) - cap padding at the captured row count.
            if (rasterHeight > packed.Count && rasterHeight <= packed.Count * 2 + 16)
                while (packed.Count < rasterHeight) packed.Add(new byte[rowBytes]);

            int height = packed.Count;
            string embedded = hpgl != null && hpgl.Length > 0 ? hpgl.ToString() : null;
            return new PclRasterImage(width, height, packed, rowBytes, resolution, embedded);
        }
    }
}
