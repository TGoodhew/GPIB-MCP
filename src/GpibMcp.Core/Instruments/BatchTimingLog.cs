using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Appends a per-batch timing breakdown to a known file (issue #58), so that after a bench sweep you can
    /// see where the wall-clock went: how many of each op ran (write/query/set/complete/wait) and the
    /// total/mean/max time per op type - which makes the dominant cost (typically the SRQ completion wait,
    /// an unavoidable instrument cost) obvious versus the per-command GPIB overhead that is ours to cut.
    /// Written best-effort on each run (batch runs are user-initiated and infrequent, like a capture, so this
    /// is not noisy); a failure never breaks the run. Lives next to the database at
    /// <c>%LOCALAPPDATA%\GpibMcp\batch-timing.log</c> (see <see cref="InstrumentPaths.BatchTimingLogPath"/>).
    /// </summary>
    public static class BatchTimingLog
    {
        public static string Path => InstrumentPaths.BatchTimingLogPath();

        /// <summary>Formats and appends one run's timing entry. Best-effort.</summary>
        public static void Write(BatchResult result, string timestamp = null)
        {
            if (result == null) return;
            try
            {
                File.AppendAllText(Path, Format(result, timestamp));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write batch timing log (" + Path + "): " + ex.Message);
            }
        }

        /// <summary>Builds the text entry (pure; unit-testable without touching the filesystem). The per-op
        /// rows are ordered by total time descending so the hotspot is first.</summary>
        public static string Format(BatchResult result, string timestamp = null)
        {
            var r = result.Ran;
            var sb = new StringBuilder();
            sb.AppendLine("==== batch " + (timestamp ?? Now()) + "  " + (r.Sweep ?? "no sweep") + " ====");
            sb.AppendLine("ran: " + r.Points + (r.Points == 1 ? " point" : " points") + ", " + r.OpsPerPoint +
                          " ops/point, " + r.GpibOps + " gpib ops, " + r.ElapsedMs + "ms total");

            var ordered = new List<BatchOpTiming>(result.Timing);
            ordered.Sort((a, b) => b.TotalMs.CompareTo(a.TotalMs));   // hotspots first

            if (ordered.Count == 0)
            {
                sb.AppendLine("per-op timing: (no ops completed)");
            }
            else
            {
                long total = 0;
                foreach (var t in ordered) total += t.TotalMs;
                sb.AppendLine("per-op timing (op: count  total  mean  max  share):");
                foreach (var t in ordered)
                {
                    double share = total > 0 ? 100.0 * t.TotalMs / total : 0;
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  {0,-9}: n={1,4}  total={2,8}ms  mean={3,6}ms  max={4,6}ms  {5,5:0.0}%",
                        t.Op, t.Count, t.TotalMs, Math.Round(t.MeanMs), t.MaxMs, share));
                }
            }

            sb.AppendLine("errors: " + result.Errors.Count);
            if (result.Truncated != null)
                sb.AppendLine("truncated: returned " + result.Truncated.Returned + " of " +
                              result.Truncated.Total + " rows (" + result.Truncated.Reason + ")");
            sb.AppendLine();
            return sb.ToString();
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
