using System;
using System.Globalization;
using System.IO;
using System.Text;
using GpibMcp.Instruments;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Diagnostics
{
    /// <summary>
    /// Appends one audit line per MCP tool call to a known file (issue from the #74 investigation): an
    /// always-on, level-independent record of WHAT was called, with status and wall-clock, so a whole turn
    /// can be reconstructed afterwards - e.g. to count single-op calls versus one <c>gpib_batch</c>, and to
    /// total the non-batched time against a batched run's <c>batch-timing.log</c>. Written best-effort (a
    /// failure never breaks the call) and unconditionally (one terse line per call is not noisy). Lives at
    /// <c>%LOCALAPPDATA%\GpibMcp\tool-calls.log</c> (see <see cref="InstrumentPaths.ToolCallLogPath"/>).
    /// </summary>
    public static class ToolCallLog
    {
        /// <summary>Longest args digest written per line; the rest is elided. Keeps the log scannable.</summary>
        private const int MaxDigestChars = 200;

        public static string Path => InstrumentPaths.ToolCallLogPath();

        /// <summary>Formats and appends one tool-call audit line. Best-effort.</summary>
        public static void Write(string tool, JObject args, bool ok, long elapsedMs, string timestamp = null)
        {
            try
            {
                File.AppendAllText(Path, Format(tool, args, ok, elapsedMs, timestamp));
            }
            catch (Exception ex)
            {
                Log.Warn("Could not write tool-call log (" + Path + "): " + ex.Message);
            }
        }

        /// <summary>Builds the single audit line (pure; unit-testable). Columns: timestamp, status, elapsed,
        /// tool name, then a compact one-line digest of the arguments.</summary>
        public static string Format(string tool, JObject args, bool ok, long elapsedMs, string timestamp = null)
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}  {1,-3}  {2,6}ms  {3,-22}  {4}{5}",
                timestamp ?? Now(),
                ok ? "ok" : "ERR",
                elapsedMs,
                tool ?? "(none)",
                Digest(args),
                Environment.NewLine);
        }

        /// <summary>A compact, single-line summary of the arguments: scalars inline (truncated), arrays as
        /// <c>key=[N]</c>, objects as <c>key={N}</c>. Bounded to <see cref="MaxDigestChars"/>.</summary>
        private static string Digest(JObject args)
        {
            if (args == null || !args.HasValues) return "-";
            var sb = new StringBuilder();
            foreach (var p in args.Properties())
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(p.Name).Append('=');
                switch (p.Value.Type)
                {
                    case JTokenType.Array:  sb.Append('[').Append(((JArray)p.Value).Count).Append(']'); break;
                    case JTokenType.Object: sb.Append('{').Append(((JObject)p.Value).Count).Append('}'); break;
                    default:                sb.Append(Scalar(p.Value)); break;
                }
                if (sb.Length > MaxDigestChars) { sb.Length = MaxDigestChars; sb.Append('…'); break; }
            }
            return sb.ToString();
        }

        /// <summary>One scalar value as a short string: trimmed, newlines collapsed, truncated to 60 chars.</summary>
        private static string Scalar(JToken value)
        {
            string s = Convert.ToString(((JValue)value).Value, CultureInfo.InvariantCulture) ?? "";
            s = s.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return s.Length > 60 ? s.Substring(0, 60) + "…" : s;
        }

        private static string Now() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
    }
}
