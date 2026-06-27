// -----------------------------------------------------------------------------
// Hpgl.Rendering - PCL raster-row re-framing for GPIB read drop-outs (#82).
//
// The PCL "print" capture is read off the GPIB in timeout-bounded chunks exactly
// like the HP-GL "plot" path; the same read seams can drop a byte (the HP 37204A
// bus extender / NI driver). For HP-GL a dropped digit shifts one coordinate and
// is healed by HpglTraceRepair. For PCL it is worse: every Transfer-Raster-Data
// row (ESC*b<n>W) declares an exact byte count, so a single dropped data byte
// leaves that row one byte short. A printer then reads the *next* row's ESC as
// this row's last raster byte, loses sync, and prints the following "*b<n>W..."
// command as literal text - the stray glyphs seen on a real hardcopy (#82). Our
// own renderer is more forgiving, so the defect only shows on the wire.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;

namespace Hpgl.Rendering
{
    /// <summary>
    /// Re-frames the Transfer-Raster-Data rows of a captured PCL byte stream so every <c>ESC*b&lt;n&gt;W</c>
    /// is followed by exactly <c>n</c> payload bytes before the next PCL command, healing the single-byte
    /// drop a GPIB read seam can introduce (#82). Re-framing is compression-agnostic - the W count is the
    /// payload byte count regardless of the compression method - so a short row is zero-padded back to its
    /// declared length and a long row is trimmed, which restores byte alignment for every downstream row.
    /// Only the framing is touched; well-formed streams are returned byte-for-byte unchanged.
    /// </summary>
    public static class PclRasterRepair
    {
        private const byte Esc = 0x1B;

        /// <summary>How far on either side of a row's declared end we hunt for the true next-command boundary.
        /// A read-seam drop is normally a single byte; the window is also capped at the row's declared length
        /// so the search can never skip past an entire following row into the wrong one.</summary>
        private const int MaxResyncWindow = 16;

        /// <summary>
        /// Returns <paramref name="data"/> with every raster row re-framed to its declared byte count, and
        /// reports how many rows were corrected. When nothing needs re-framing the input array is returned
        /// unchanged (same reference).
        /// </summary>
        public static byte[] Repair(byte[] data, out int reframed)
        {
            reframed = 0;
            if (data == null || data.Length == 0) return data;

            var outp = new List<byte>(data.Length + 16);
            int p = 0, n = data.Length;

            while (p < n)
            {
                byte b = data[p];
                if (b != Esc) { outp.Add(b); p++; continue; }

                // Copy the escape introducer.
                outp.Add(b); p++;
                if (p >= n) break;

                byte c = data[p];
                if (c >= 33 && c <= 47)
                {
                    // Parameterized sequence: ESC <param 33-47> [group 96-126] {value <term>} ...
                    outp.Add(c); p++;
                    byte group = 0;
                    if (p < n && data[p] >= 96 && data[p] <= 126) { group = data[p]; outp.Add(data[p]); p++; }

                    while (p < n)
                    {
                        int value = CopyValue(data, ref p, outp);
                        if (p >= n) break;
                        byte term = data[p]; outp.Add(term); p++;
                        bool terminates = term >= 64 && term <= 94;            // uppercase ends the sequence
                        char cmd = terminates ? (char)term : (char)(term - 32); // lowercase = combined param

                        // ESC*b<n>W transfers a raster row of <n> payload bytes - the one command we re-frame.
                        if (c == '*' && group == 'b' && cmd == 'W')
                            p = EmitRow(data, p, value, outp, ref reframed);

                        if (terminates) break;
                    }
                }
                else
                {
                    // Two-character escape (e.g. ESC E reset, ESC =): no payload, copy the command char.
                    outp.Add(c); p++;
                }
            }

            if (reframed == 0) return data;     // untouched - hand back the original bytes
            return outp.ToArray();
        }

        /// <summary>Convenience overload when the re-frame count is not needed.</summary>
        public static byte[] Repair(byte[] data) => Repair(data, out _);

        /// <summary>
        /// Emits exactly <paramref name="declared"/> payload bytes for one transfer row, padding a short row
        /// with zeros or trimming a long one, and returns the input position of the next command. The true
        /// next-command boundary is located by re-synchronising on the following PCL escape; when the row is
        /// already well-framed the bytes are copied verbatim.
        /// </summary>
        private static int EmitRow(byte[] data, int dataStart, int declared, List<byte> outp, ref int reframed)
        {
            int n = data.Length;
            if (declared < 0) declared = 0;
            int declaredEnd = dataStart + declared;

            int boundary = FindResyncBoundary(data, dataStart, declaredEnd, declared);
            if (boundary < 0)
            {
                // Well-framed (or no confident re-sync point): copy the declared bytes verbatim, clamped to EOF.
                int avail = Math.Min(declared, n - dataStart);
                for (int k = 0; k < avail; k++) outp.Add(data[dataStart + k]);
                return dataStart + avail;
            }

            // Misframed: the real payload runs dataStart..boundary. Re-emit exactly `declared` bytes so the
            // next command lands on its boundary again - copy the real bytes, then zero-pad (short) or drop
            // the surplus (long). The one affected row may be a dot or two off; every later row realigns.
            int actual = boundary - dataStart;
            int copy = Math.Min(actual, declared);
            for (int k = 0; k < copy; k++) outp.Add(data[dataStart + k]);
            for (int k = copy; k < declared; k++) outp.Add(0);
            reframed++;
            return boundary;
        }

        /// <summary>
        /// Returns the index of the PCL command that should follow a row whose payload is <paramref name="declared"/>
        /// bytes, or -1 when the row is already well-framed (the declared end is the stream end or a command start)
        /// or no confident boundary is found. A read-seam drop shifts the true boundary a byte or two off the
        /// declared end, so the nearest command-start escape within a bounded window is the row's real end.
        /// </summary>
        private static int FindResyncBoundary(byte[] data, int dataStart, int declaredEnd, int declared)
        {
            int n = data.Length;
            // At or past EOF, or already sitting on a command start: nothing to fix.
            if (declaredEnd >= n) return -1;
            if (IsCommandStart(data, declaredEnd)) return -1;

            int window = Math.Min(MaxResyncWindow, Math.Max(1, declared));
            for (int d = 1; d <= window; d++)
            {
                int lo = declaredEnd - d;                       // short row (dropped byte) - most common
                if (lo > dataStart && IsCommandStart(data, lo)) return lo;
                int hi = declaredEnd + d;                        // long row (inserted byte)
                if (hi < n && IsCommandStart(data, hi)) return hi;
            }
            return -1;   // no confident boundary - leave the row alone
        }

        /// <summary>
        /// True when <paramref name="i"/> is an ESC that introduces a recognised PCL command. The introducer
        /// set is deliberately narrow (the sequences instrument print streams actually use) so a raw raster
        /// byte that merely happens to be 0x1B is not mistaken for a command boundary.
        /// </summary>
        private static bool IsCommandStart(byte[] data, int i)
        {
            if (data[i] != Esc || i + 1 >= data.Length) return false;
            byte c = data[i + 1];
            return c == (byte)'*' || c == (byte)'&' || c == (byte)'(' || c == (byte)')'
                || c == (byte)'%' || c == (byte)'E' || c == (byte)'=';
        }

        /// <summary>Copies an optional signed numeric value field to <paramref name="outp"/>, returning its
        /// integer part and leaving <paramref name="p"/> on the terminator (mirrors PclRasterDecoder.ReadValue).</summary>
        private static int CopyValue(byte[] d, ref int p, List<byte> outp)
        {
            int n = d.Length;
            bool neg = false;
            if (p < n && (d[p] == '+' || d[p] == '-')) { neg = d[p] == '-'; outp.Add(d[p]); p++; }
            long v = 0;
            while (p < n && d[p] >= '0' && d[p] <= '9') { v = v * 10 + (d[p] - '0'); outp.Add(d[p]); p++; }
            if (p < n && d[p] == '.') { outp.Add(d[p]); p++; while (p < n && d[p] >= '0' && d[p] <= '9') { outp.Add(d[p]); p++; } }
            return (int)(neg ? -v : v);
        }
    }
}
