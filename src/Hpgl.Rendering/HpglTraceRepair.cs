// -----------------------------------------------------------------------------
// Hpgl.Rendering - trace-coordinate repair for GPIB read drop-outs (#79).
//
// The HP-GL plotter-emulation capture-and-render technique is derived from the
// HP7470A Plotter Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>
    /// Repairs a single corrupted X coordinate in a spectrum/network trace polyline.
    ///
    /// A byte dropped on the GPIB read at a chunked-read seam (confirmed in #79: every streaming read is a
    /// timeout-partial chunk, and a dropped digit lands on a read boundary) shortens an X value - e.g.
    /// "995" -&gt; "95" - so the pen jumps backwards toward the page edge and then recovers on the next
    /// point, drawing a stray excursion. A trace is a long run of pen-down coordinate pairs whose X is a
    /// strictly increasing regular grid, so an interior vertex whose X falls outside the [left, right]
    /// neighbour bracket is unambiguously corrupt; its true X is the neighbour midpoint. We restore that X
    /// and keep the (genuine) Y sample, so the amplitude point survives and only the excursion is removed.
    ///
    /// Only long coordinate runs (a real trace) are considered - graticule lines legitimately run
    /// right-to-left and short marker/annotation moves are left byte-for-byte intact. Amplitude (Y) spikes
    /// are never touched: they are indistinguishable from a real signal peak. The repaired HP-GL is used
    /// for both the rendered image and the bytes handed back via return_hpgl_base64, so a plot forwarded to
    /// a plotter is excursion-free too; the verbatim debug dump keeps the unrepaired capture.
    /// </summary>
    public static class HpglTraceRepair
    {
        private const char Esc = '';   // device-control escape introducer
        private const char Etx = '';   // default LB (label) terminator

        /// <summary>
        /// Minimum coordinate-pair count for a run to be treated as a trace polyline. The 8560/8563/8720
        /// traces are 400-600+ points; graticules and markers are &lt;= 3 pairs, so this cleanly isolates the
        /// trace without ever touching the frame.
        /// </summary>
        private const int TracePairThreshold = 32;

        /// <summary>
        /// Returns <paramref name="hpgl"/> with any single-point X excursions in trace polylines repaired,
        /// and reports how many vertices were corrected. Non-trace bytes are preserved exactly; when nothing
        /// is repaired the original string is returned unchanged.
        /// </summary>
        public static string Repair(string hpgl, out int repaired)
        {
            repaired = 0;
            if (string.IsNullOrEmpty(hpgl)) return hpgl;

            char terminator = Etx;
            int i = 0, n = hpgl.Length;
            StringBuilder outSb = null;   // built lazily on the first edit
            int copiedUpto = 0;

            while (i < n)
            {
                char c = hpgl[i];

                // Device-control escape sequences (ESC . <cmd> ... [: ; ESC]) - skip, never geometry.
                if (c == Esc)
                {
                    i++;
                    if (i < n && hpgl[i] == '.') i++;
                    if (i < n) i++;                                              // the command char
                    while (i < n && hpgl[i] != ':' && hpgl[i] != Esc && hpgl[i] != ';') i++;
                    if (i < n && (hpgl[i] == ':' || hpgl[i] == ';')) i++;
                    continue;
                }
                if (!char.IsLetter(c)) { i++; continue; }
                if (i + 1 >= n) break;
                char b = hpgl[i + 1];
                if (!char.IsLetter(b)) { i++; continue; }

                string mnem = new string(new[] { char.ToUpperInvariant(c), char.ToUpperInvariant(b) });
                int afterMnem = i + 2;

                // Payload-bearing mnemonics whose text can contain digits - skip their payloads so a label
                // like "300Hz" or an encoded PE block is never mistaken for coordinates.
                if (mnem == "LB")
                {
                    int j = afterMnem;
                    while (j < n && hpgl[j] != terminator) j++;
                    if (j < n) j++;                                             // consume terminator
                    i = j; continue;
                }
                if (mnem == "PE")
                {
                    int j = afterMnem;
                    while (j < n && hpgl[j] != ';') j++;
                    if (j < n) j++;
                    i = j; continue;
                }
                if (mnem == "SM")
                {
                    int j = afterMnem;
                    if (j < n && hpgl[j] != ';') j++;                           // the symbol char
                    if (j < n && hpgl[j] == ';') j++;
                    i = j; continue;
                }
                if (mnem == "DT")
                {
                    int j = afterMnem;
                    if (j < n && hpgl[j] != ';') { terminator = hpgl[j]; j++; while (j < n && hpgl[j] != ';') j++; }
                    else terminator = Etx;
                    if (j < n && hpgl[j] == ';') j++;
                    i = j; continue;
                }

                // Generic mnemonic: numeric parameters up to ';' or the next letter.
                int start = afterMnem, end = afterMnem;
                while (end < n && hpgl[end] != ';' && !char.IsLetter(hpgl[end])) end++;

                if (IsPenMove(mnem))
                {
                    string repl = RepairRun(hpgl, start, end, ref repaired);
                    if (repl != null)
                    {
                        if (outSb == null) outSb = new StringBuilder(n + 16);
                        outSb.Append(hpgl, copiedUpto, start - copiedUpto);
                        outSb.Append(repl);
                        copiedUpto = end;
                    }
                }

                i = end;   // the trailing ';' (if any) is a non-letter and is skipped next iteration
            }

            if (outSb == null) return hpgl;
            outSb.Append(hpgl, copiedUpto, n - copiedUpto);
            return outSb.ToString();
        }

        /// <summary>Convenience overload when the repair count is not needed.</summary>
        public static string Repair(string hpgl) => Repair(hpgl, out _);

        private static bool IsPenMove(string mnem) =>
            mnem == "PA" || mnem == "PD" || mnem == "PU" || mnem == "PR";

        /// <summary>
        /// Repairs the comma-separated coordinate run <c>hpgl[start..end)</c> if it is long enough to be a
        /// trace and contains an isolated out-of-order X. Returns the rewritten run text, or null to leave
        /// the run untouched (too short, unparseable, space-separated, or already clean).
        /// </summary>
        private static string RepairRun(string hpgl, int start, int end, ref int repaired)
        {
            if (end - start < 3) return null;
            string run = hpgl.Substring(start, end - start);

            string[] tok = run.Split(',');
            int pairs = tok.Length / 2;
            if (pairs < TracePairThreshold) return null;      // not a trace - leave alone

            // Parse every X (even index). Any unparseable token (e.g. a space-separated or run-on stream we
            // can't safely rewrite) aborts the repair so we never mangle a run we don't fully understand.
            var xs = new double[pairs];
            for (int k = 0; k < pairs; k++)
            {
                if (!double.TryParse(tok[2 * k], NumberStyles.Float, CultureInfo.InvariantCulture, out xs[k]))
                    return null;
            }

            bool changed = false;
            for (int k = 1; k < pairs - 1; k++)
            {
                double xl = xs[k - 1], xc = xs[k], xr = xs[k + 1];
                // Isolated spike: the neighbours agree (non-decreasing) but this X sits outside their
                // bracket. A dropped digit yields xc < xl; a digit run-on yields xc > xr. Either way the
                // true grid X is the neighbour midpoint - restore it and keep the genuine Y.
                if (xl <= xr && (xc < xl || xc > xr))
                {
                    double fixedX = Math.Round((xl + xr) / 2.0, MidpointRounding.AwayFromZero);
                    tok[2 * k] = fixedX.ToString("0", CultureInfo.InvariantCulture);
                    xs[k] = fixedX;
                    repaired++;
                    changed = true;
                }
            }

            return changed ? string.Join(",", tok) : null;
        }
    }
}
