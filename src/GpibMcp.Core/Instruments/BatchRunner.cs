using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace GpibMcp.Instruments
{
    // ---- plan model (parsed from the gpib_batch tool args) ----------------------

    /// <summary>One ordered operation in a batch plan (#59).</summary>
    public sealed class BatchStep
    {
        public string Op;         // write | query | read | set | complete | wait
        public string Resource;   // VISA resource ("GPIB0::18::INSTR") or an assigned model name ("8563E")
        public string Command;    // write/query: literal wire string; set: command name (for the resolver)
        public string As;         // query/read: capture name -> a result column and {{as}} for later steps
        public string Value;      // set: the value (may contain {{var}}/{{capture}}), parsed to a number
        public string Unit;       // set: the human unit for the resolver; query/read: optional column-unit hint
        public string Operation;  // complete: the statusModel operation name (e.g. "sweepComplete")
        public int SettleMs;      // optional post-step settle; the whole step for op=wait
        public int? TimeoutMs;    // optional per-step I/O timeout
    }

    /// <summary>A single swept dimension: VAR runs FROM..TO by STEP (or a COUNT of points).</summary>
    public sealed class BatchSweep
    {
        public string Var = "x";
        public double From;
        public double To;
        public double Step;
        public int? Count;
        public string Unit;       // optional unit label for the swept column
    }

    /// <summary>A whole batch: an optional sweep + the ordered per-point steps.</summary>
    public sealed class BatchPlan
    {
        public BatchSweep Sweep;
        public List<BatchStep> Steps = new List<BatchStep>();
        public string OnError = "stop";   // "stop" | "continue"
    }

    /// <summary>Hard ceilings (a runaway plan can't drown the bus or the token budget).</summary>
    public sealed class BatchCaps
    {
        public int MaxPoints = 1000;
        public int MaxOpsPerPoint = 16;
        public int MaxTotalOps = 5000;
        public int MaxRows = 500;
        public int DefaultStepTimeoutMs = 5000;
    }

    // ---- execution boundary (real impl wraps the bus; a fake drives tests) ------

    /// <summary>
    /// Runs one batch step against the instruments. The real implementation wraps the instrument
    /// manager (resolving model names, the value resolver, and the SRQ completion waiter); a fake
    /// drives the runner headlessly in tests. Methods throw on failure - <see cref="BatchRunner"/>
    /// catches and records the error so a partial run is still useful.
    /// </summary>
    public interface IBatchExecutor
    {
        void Write(string resource, string command, int timeoutMs);
        string Query(string resource, string command, int timeoutMs);   // write + read, returns the response
        void Set(string resource, string command, double value, string unit, int timeoutMs);
        void Complete(string resource, string operation, int timeoutMs);
        void Sleep(int ms);
    }

    // ---- result model (serialised to the JSON envelope by the tool) -------------

    public sealed class BatchColumn
    {
        public string Name;
        public string Unit;       // null when unknown
        public string From;       // optional provenance, e.g. "GPIB0::18 8563E"
    }

    public sealed class BatchError
    {
        public int Point;         // 0-based sweep point index (0 when no sweep)
        public int Step;          // 0-based step index within the point
        public string Op;
        public string Resource;
        public string Command;
        public string Error;
    }

    public sealed class BatchTruncation
    {
        public int Returned;
        public int Total;
        public string Reason;
    }

    public sealed class BatchRanInfo
    {
        public string Sweep;
        public int Points;
        public int OpsPerPoint;
        public int GpibOps;
        public long ElapsedMs;
    }

    public sealed class BatchResult
    {
        public bool Ok;
        public BatchRanInfo Ran = new BatchRanInfo();
        public List<BatchColumn> Columns = new List<BatchColumn>();
        public List<List<object>> Rows = new List<List<object>>();
        public List<BatchError> Errors = new List<BatchError>();
        public BatchTruncation Truncated;   // null unless rows were capped
    }

    /// <summary>
    /// Backend-neutral batch engine (#59): expands an optional sweep, runs the ordered steps per point
    /// through an <see cref="IBatchExecutor"/>, interpolates <c>{{var}}</c>/<c>{{capture}}</c> into
    /// commands, captures <c>as</c> readings into a result table, records per-step errors, and enforces
    /// caps. No hardware/VISA dependency, so it is fully unit-testable with a scripted fake executor
    /// (the same pattern as <see cref="ScreenCapture"/>).
    /// </summary>
    public static class BatchRunner
    {
        private static readonly Regex Placeholder = new Regex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);
        private static readonly Regex Number = new Regex(@"[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", RegexOptions.Compiled);

        /// <summary>Validates the plan against the caps; returns a human reason, or null if it is runnable.</summary>
        public static string Validate(BatchPlan plan, BatchCaps caps)
        {
            if (plan == null) return "no plan.";
            if (plan.Steps == null || plan.Steps.Count == 0) return "the batch has no steps.";
            int points = ExpandSweep(plan.Sweep, out string sweepErr).Count;
            if (sweepErr != null) return sweepErr;
            if (points > caps.MaxPoints) return "sweep has " + points + " points; the cap is " + caps.MaxPoints + ".";
            if (plan.Steps.Count > caps.MaxOpsPerPoint)
                return plan.Steps.Count + " steps per point; the cap is " + caps.MaxOpsPerPoint + ".";
            long total = (long)points * plan.Steps.Count;
            if (total > caps.MaxTotalOps) return total + " total operations; the cap is " + caps.MaxTotalOps + ".";
            return null;
        }

        /// <summary>A 1-line summary of what a plan would run, for preview/confirm.</summary>
        public static BatchRanInfo Preview(BatchPlan plan, BatchCaps caps)
        {
            var pts = ExpandSweep(plan.Sweep, out _);
            return new BatchRanInfo
            {
                Sweep = DescribeSweep(plan.Sweep),
                Points = pts.Count,
                OpsPerPoint = plan.Steps != null ? plan.Steps.Count : 0,
                GpibOps = pts.Count * (plan.Steps != null ? plan.Steps.Count : 0)
            };
        }

        public static BatchResult Run(BatchPlan plan, IBatchExecutor exec, BatchCaps caps, Func<long> nowMs)
        {
            caps = caps ?? new BatchCaps();
            var result = new BatchResult { Ok = true };
            long start = nowMs();

            var points = ExpandSweep(plan.Sweep, out string sweepErr);
            result.Ran.Sweep = DescribeSweep(plan.Sweep);
            result.Ran.Points = points.Count;
            result.Ran.OpsPerPoint = plan.Steps.Count;

            // Column layout: the swept var first (if any), then each distinct `as` in first-seen order.
            var colIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            if (plan.Sweep != null)
            {
                result.Columns.Add(new BatchColumn { Name = plan.Sweep.Var, Unit = plan.Sweep.Unit });
                colIndex[plan.Sweep.Var] = 0;
            }
            foreach (var s in plan.Steps)
                if (!string.IsNullOrEmpty(s.As) && !colIndex.ContainsKey(s.As))
                {
                    colIndex[s.As] = result.Columns.Count;
                    result.Columns.Add(new BatchColumn { Name = s.As, Unit = string.IsNullOrEmpty(s.Unit) ? null : s.Unit, From = s.Resource });
                }

            bool stopOnError = !string.Equals(plan.OnError, "continue", StringComparison.OrdinalIgnoreCase);
            int gpibOps = 0;
            bool aborted = false;

            for (int pi = 0; pi < points.Count && !aborted; pi++)
            {
                var bindings = new Dictionary<string, string>(StringComparer.Ordinal);
                var numeric = new Dictionary<string, object>(StringComparer.Ordinal);
                if (plan.Sweep != null)
                {
                    bindings[plan.Sweep.Var] = FormatNumber(points[pi]);
                    numeric[plan.Sweep.Var] = points[pi];
                }

                var row = new List<object>(new object[result.Columns.Count]);
                if (plan.Sweep != null) row[0] = points[pi];

                for (int si = 0; si < plan.Steps.Count; si++)
                {
                    var step = plan.Steps[si];
                    int timeout = step.TimeoutMs ?? caps.DefaultStepTimeoutMs;
                    gpibOps++;
                    try
                    {
                        string cmd = Interpolate(step.Command, bindings);
                        switch ((step.Op ?? "").ToLowerInvariant())
                        {
                            case "write":
                                exec.Write(step.Resource, cmd, timeout);
                                break;
                            case "query":
                            case "read":
                                string resp = exec.Query(step.Resource, cmd, timeout);
                                if (!string.IsNullOrEmpty(step.As))
                                {
                                    object cell = ParseCell(resp);
                                    bindings[step.As] = cell is double d ? FormatNumber(d) : Convert.ToString(cell, CultureInfo.InvariantCulture);
                                    numeric[step.As] = cell;
                                    row[colIndex[step.As]] = cell;
                                }
                                break;
                            case "set":
                                double val = ParseRequired(Interpolate(step.Value, bindings), step);
                                exec.Set(step.Resource, step.Command, val, step.Unit, timeout);
                                break;
                            case "complete":
                                exec.Complete(step.Resource, step.Operation, step.TimeoutMs ?? 30000);
                                break;
                            case "wait":
                                exec.Sleep(step.SettleMs);
                                break;
                            default:
                                throw new InvalidOperationException("unknown op '" + step.Op + "'");
                        }
                        if (step.SettleMs > 0 && !string.Equals(step.Op, "wait", StringComparison.OrdinalIgnoreCase))
                            exec.Sleep(step.SettleMs);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add(new BatchError
                        {
                            Point = pi, Step = si, Op = step.Op, Resource = step.Resource,
                            Command = step.Op == "set" ? step.Command : Interpolate(step.Command, bindings),
                            Error = ex.Message
                        });
                        result.Ok = false;
                        if (stopOnError) { aborted = true; break; }
                        break;   // skip the rest of THIS point (later steps likely depend on the failed one)
                    }
                }

                if (result.Rows.Count >= caps.MaxRows)
                {
                    result.Truncated = new BatchTruncation { Returned = caps.MaxRows, Total = points.Count, Reason = "row cap" };
                }
                else
                {
                    result.Rows.Add(row);
                }
            }

            result.Ran.GpibOps = gpibOps;
            result.Ran.ElapsedMs = nowMs() - start;
            return result;
        }

        // ---- helpers ------------------------------------------------------------

        /// <summary>Expands the sweep into its point values (inclusive of TO within rounding). Empty sweep -&gt; one null point.</summary>
        public static List<double> ExpandSweep(BatchSweep s, out string error)
        {
            error = null;
            var pts = new List<double>();
            if (s == null) { pts.Add(0); return pts; }   // a single "point" so no-sweep batches run once
            if (s.Count.HasValue)
            {
                int n = s.Count.Value;
                if (n <= 0) { error = "sweep count must be > 0."; return pts; }
                double step = n == 1 ? 0 : (s.To - s.From) / (n - 1);
                for (int i = 0; i < n; i++) pts.Add(s.From + i * step);
                return pts;
            }
            if (s.Step == 0) { error = "sweep step must be non-zero (or use count)."; return pts; }
            if ((s.To - s.From) * s.Step < 0) { error = "sweep step direction does not reach 'to'."; return pts; }
            double eps = Math.Abs(s.Step) * 1e-9;
            for (double v = s.From; (s.Step > 0 ? v <= s.To + eps : v >= s.To - eps); v += s.Step)
            {
                pts.Add(v);
                if (pts.Count > 100000) { error = "sweep produced too many points."; break; }
            }
            return pts;
        }

        private static string DescribeSweep(BatchSweep s)
        {
            if (s == null) return null;
            if (s.Count.HasValue) return s.Var + " " + FormatNumber(s.From) + ".." + FormatNumber(s.To) + " (" + s.Count.Value + " pts)";
            return s.Var + " " + FormatNumber(s.From) + ".." + FormatNumber(s.To) + " step " + FormatNumber(s.Step);
        }

        private static string Interpolate(string template, Dictionary<string, string> bindings)
        {
            if (string.IsNullOrEmpty(template)) return template;
            return Placeholder.Replace(template, m =>
            {
                string key = m.Groups[1].Value;
                return bindings.TryGetValue(key, out string v) ? v : m.Value;   // leave unknown placeholders intact
            });
        }

        /// <summary>Parses an instrument response into a numeric cell when possible, else the trimmed raw string.</summary>
        private static object ParseCell(string response)
        {
            if (response == null) return null;
            string trimmed = response.Trim();
            var m = Number.Match(trimmed);
            if (m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            return trimmed;
        }

        private static double ParseRequired(string text, BatchStep step)
        {
            if (double.TryParse((text ?? "").Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
                return d;
            throw new InvalidOperationException("set value '" + text + "' is not a number");
        }

        /// <summary>Formats a value without floating-point cruft (integer when whole), shared with the resolver style.</summary>
        public static string FormatNumber(double value)
        {
            if (value == Math.Floor(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e15)
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }
    }
}
