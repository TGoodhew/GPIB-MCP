using System;
using System.Globalization;
using System.IO;
using System.Text;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Appends a detailed per-capture timing breakdown to a known file (issue #53), so the time spent in
    /// the capture loop - instrument warm-up vs. streaming vs. completion tail, and every individual read -
    /// can be inspected after a bench capture. Written unconditionally on each capture (captures are
    /// user-initiated and infrequent, so this is not noisy) and best-effort: a failure never breaks a
    /// capture. The file lives next to the database/bindings at
    /// <c>%LOCALAPPDATA%\GpibMcp\capture-timing.log</c> (see <see cref="InstrumentPaths.CaptureTimingLogPath"/>).
    /// </summary>
    public static class CaptureTimingLog
    {
        public static string Path => InstrumentPaths.CaptureTimingLogPath();

        /// <summary>Formats and appends one capture's timing entry. <paramref name="renderMs"/>/<paramref name="saveMs"/>
        /// confirm the render and save stages are negligible relative to the capture loop.</summary>
        public static void Write(string resource, string model, string mode, string command,
                                 CaptureResult result, long renderMs, long saveMs, string timestamp = null)
        {
            if (result == null) return;
            try
            {
                File.AppendAllText(Path, Format(resource, model, mode, command, result, renderMs, saveMs, timestamp));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write capture timing log (" + Path + "): " + ex.Message);
            }
        }

        /// <summary>Builds the text entry (pure; unit-testable without touching the filesystem).</summary>
        public static string Format(string resource, string model, string mode, string command,
                                    CaptureResult result, long renderMs, long saveMs, string timestamp = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("==== capture " + (timestamp ?? Now()) + "  " + model + " (" + mode + ")  " + resource + " ====");
            sb.AppendLine("command: " + command);

            var d = result.Diagnostics;
            if (d != null)
            {
                sb.AppendLine(d.SummaryLine("capture " + mode) +
                              ", render " + renderMs + "ms, save " + saveMs + "ms");
                sb.AppendLine("completion: " + result.Completion);
                sb.AppendLine("per-read trace (idx: at +Tms  gap Gms  took Ems  -> B bytes [timeout]):");
                foreach (var r in d.Reads)
                    sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                        "  #{0,-3} at {1,7}ms  gap {2,5}ms  took {3,5}ms  -> {4,6} bytes{5}",
                        r.Index, r.AtMs, r.GapMs, r.ElapsedMs, r.Bytes, r.TimedOut ? "  [timeout]" : ""));
            }
            else
            {
                sb.AppendLine("(no per-read diagnostics on this path) bytes=" + result.ByteCount + ", " +
                              result.Completion + ", capture " + result.ElapsedMs + "ms, render " + renderMs +
                              "ms, save " + saveMs + "ms");
            }
            sb.AppendLine();
            return sb.ToString();
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }
}
