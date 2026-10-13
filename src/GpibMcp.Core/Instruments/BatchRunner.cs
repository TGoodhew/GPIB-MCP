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

        /// <summary>How long the batch may run before it stops and returns what it has; null = the default (#145).</summary>
        public int? TimeBudgetMs;
    }

    /// <summary>Hard ceilings (a runaway plan can't drown the bus or the token budget).</summary>
    public sealed class BatchCaps
    {
        public int MaxPoints = 1000;
        public int MaxOpsPerPoint = 16;
        public int MaxTotalOps = 5000;
        public int MaxRows = 500;
        public int DefaultStepTimeoutMs = 5000;

        /// <summary>Plans whose total GPIB ops exceed this are gated for confirmation before touching the
        /// bus: the tool returns a preview and runs only when re-called with confirm:true (#59 Phase 2).
        /// A small plan stays frictionless. Not a hard ceiling - confirm:true overrides it.</summary>
        public int ConfirmAboveOps = 50;

        /// <summary>
        /// How long a batch runs before it stops and returns the rows it has (#145). A client gives up on a tool
        /// call after a fixed time and the whole result is lost: a batch that returned at 34 s failed in Claude
        /// Desktop, and the link to a computer cuts off at 60 s. 25 s keeps under both.
        /// </summary>
        public int DefaultTimeBudgetMs = 25000;

        /// <summary>The most a plan may ask for: under the 60 s at which the link to a computer cuts off.</summary>
        public int MaxTimeBudgetMs = 55000;
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

    /// <summary>Why and where a batch stopped before running every point (#145).</summary>
    public sealed class BatchStop
    {
        public string Reason;     // "time budget"
        public long BudgetMs;
        public int AtPoint;       // 0-based: the first point not completed - run again from here
        public double? AtValue;   // that point's sweep value, when there is a sweep
        public string Waiting;    // the operation still running when the budget ran out, if it stopped mid-wait

        /// <summary>
        /// The sweep that runs exactly the points not yet completed, when there is a sweep. Not just a new
        /// 'from': a count-based sweep with its original count would re-space the grid (#145).
        /// </summary>
        public BatchSweep ContinueSweep;
    }

    public sealed class BatchRanInfo
    {
        public string Sweep;
        public int Points;
        public int OpsPerPoint;
        public int GpibOps;
        public long ElapsedMs;
    }

    /// <summary>Wall-clock spent in one op type across the whole run (#58 instrumentation): how many of
    /// that op ran and the total/mean/max time, so a sweep's hotspots (e.g. completion waits) are visible.</summary>
    public sealed class BatchOpTiming
    {
        public string Op;
        public int Count;
        public long TotalMs;
        public long MaxMs;
        public double MeanMs => Count > 0 ? (double)TotalMs / Count : 0;
    }

    public sealed class BatchResult
    {
        public bool Ok;
        public BatchRanInfo Ran = new BatchRanInfo();
        public List<BatchColumn> Columns = new List<BatchColumn>();
        public List<List<object>> Rows = new List<List<object>>();
        public List<BatchError> Errors = new List<BatchError>();
        public BatchTruncation Truncated;   // null unless rows were capped
        public BatchStop Stopped;           // null unless the run stopped before its last point (#145)
        public List<BatchOpTiming> Timing = new List<BatchOpTiming>();   // per-op-type wall-clock (#58)
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
            if (plan.TimeBudgetMs.HasValue && (plan.TimeBudgetMs.Value < 1000 || plan.TimeBudgetMs.Value > caps.MaxTimeBudgetMs))
                return "the time budget must be 1 to " + caps.MaxTimeBudgetMs / 1000 + " s.";
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

        /// <param name="onPoint">
        /// Optional progress callback invoked as each sweep point starts, with (0-based point, total points).
        /// A sweep can run for minutes, so this is what lets the caller report progress instead of going
        /// silent (#112). Omit it and the run behaves exactly as before.
        /// </param>
        public static BatchResult Run(BatchPlan plan, IBatchExecutor exec, BatchCaps caps, Func<long> nowMs,
                                      Action<int, int> onPoint = null)
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

            // Stop with the rows so far rather than run past the time a client waits for a tool call, which
            // would lose the whole result (#145). Waits are cut short at the budget; a point stopped part-way
            // isn't a row, and the result says which point to run again from.
            long budget = plan.TimeBudgetMs ?? caps.DefaultTimeBudgetMs;
            Func<long> left = () => budget - (nowMs() - start);

            for (int pi = 0; pi < points.Count && !aborted; pi++)
            {
                if (left() <= 0) { result.Stopped = StopAt(budget, pi, points, plan, null); break; }
                if (onPoint != null) onPoint(pi, points.Count);
                bool pointDone = true;

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
                    if (left() <= 0)
                    {
                        result.Stopped = StopAt(budget, pi, points, plan, null);
                        pointDone = false; aborted = true; break;
                    }
                    // An I/O step's timeout is capped to the budget too, or one started just before it ran out
                    // could hold the call past the client's limit for its whole timeout.
                    int wantedIo = step.TimeoutMs ?? caps.DefaultStepTimeoutMs;
                    int timeout = (int)Math.Max(1, Math.Min(wantedIo, left()));
                    gpibOps++;
                    long opStart = nowMs();
                    string cutShort = null;   // what was cut short at the budget, while it lasts
                    try
                    {
                        string cmd = Interpolate(step.Command, bindings);
                        string ioCut = timeout < wantedIo ? step.Op + (string.IsNullOrEmpty(cmd) ? "" : " " + cmd) : null;
                        switch ((step.Op ?? "").ToLowerInvariant())
                        {
                            case "write":
                                cutShort = ioCut;
                                exec.Write(step.Resource, cmd, timeout);
                                cutShort = null;
                                break;
                            case "query":
                            case "read":
                                cutShort = ioCut;
                                string resp = exec.Query(step.Resource, cmd, timeout);
                                cutShort = null;
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
                                cutShort = timeout < wantedIo ? "set " + step.Command : null;
                                exec.Set(step.Resource, step.Command, val, step.Unit, timeout);
                                cutShort = null;
                                break;
                            case "complete":
                                int wanted = step.TimeoutMs ?? 30000;
                                int allowed = (int)Math.Max(0, Math.Min(wanted, left()));
                                if (allowed < wanted) cutShort = step.Operation;
                                exec.Complete(step.Resource, step.Operation, allowed);
                                cutShort = null;   // it finished inside the budget
                                break;
                            case "wait":
                                if (!SleepWithin(exec, step.SettleMs, left)) cutShort = "wait";
                                break;
                            default:
                                throw new InvalidOperationException("unknown op '" + step.Op + "'");
                        }
                        if (cutShort == null && step.SettleMs > 0 && !string.Equals(step.Op, "wait", StringComparison.OrdinalIgnoreCase)
                            && !SleepWithin(exec, step.SettleMs, left))
                            cutShort = "settle";
                        AddTiming(result.Timing, step.Op, nowMs() - opStart);   // attribute this step's wall-clock (#58)
                        if (cutShort != null)
                        {
                            result.Stopped = StopAt(budget, pi, points, plan, null);
                            pointDone = false; aborted = true; break;
                        }
                    }
                    catch (Exception ex)
                    {
                        string error = ex.Message;
                        if (cutShort != null && left() <= 0)
                        {
                            // A step cut short at the budget ran out: what it started may well still be running.
                            error = "The batch's " + budget / 1000 + " s time budget ran out while waiting for '" + cutShort +
                                    "', which may still be running. " + ex.Message;
                            result.Stopped = StopAt(budget, pi, points, plan, cutShort);
                            pointDone = false;
                        }
                        result.Errors.Add(new BatchError
                        {
                            Point = pi, Step = si, Op = step.Op, Resource = step.Resource,
                            Command = step.Op == "set" ? step.Command : Interpolate(step.Command, bindings),
                            Error = error
                        });
                        result.Ok = false;
                        if (stopOnError || result.Stopped != null) { aborted = true; break; }
                        break;   // skip the rest of THIS point (later steps likely depend on the failed one)
                    }
                }

                if (!pointDone) break;   // stopped part-way: not a row; Stopped.AtPoint says to run it again

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
            if (result.Stopped != null) result.Ok = false;
            return result;
        }

        // ---- helpers ------------------------------------------------------------

        private static BatchStop StopAt(long budget, int point, List<double> points, BatchPlan plan, string waiting) =>
            new BatchStop
            {
                Reason = "time budget", BudgetMs = budget, AtPoint = point,
                AtValue = plan.Sweep != null ? points[point] : (double?)null, Waiting = waiting,
                ContinueSweep = RemainingSweep(plan.Sweep, points, point)
            };

        /// <summary>
        /// A sweep over exactly points[from..]: the same step from there, or - for a count-based sweep - the
        /// remaining count over the remaining range, which keeps the original spacing.
        /// </summary>
        public static BatchSweep RemainingSweep(BatchSweep sweep, List<double> points, int from)
        {
            if (sweep == null || from >= points.Count) return null;
            var rest = new BatchSweep { Var = sweep.Var, From = points[from], To = sweep.To, Unit = sweep.Unit };
            if (sweep.Count.HasValue) rest.Count = points.Count - from;
            else rest.Step = sweep.Step;
            return rest;
        }

        /// <summary>Sleeps <paramref name="ms"/>, or only what is left of the budget; false when cut short.</summary>
        private static bool SleepWithin(IBatchExecutor exec, int ms, Func<long> left)
        {
            if (ms <= 0) return true;
            long remaining = left();
            if (ms <= remaining) { exec.Sleep(ms); return true; }
            if (remaining > 0) exec.Sleep((int)remaining);
            return false;
        }

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

        /// <summary>Folds one executed step's wall-clock into the per-op-type aggregate (first-seen order).</summary>
        private static void AddTiming(List<BatchOpTiming> timing, string op, long ms)
        {
            if (ms < 0) ms = 0;
            string key = (op ?? "").ToLowerInvariant();
            BatchOpTiming t = null;
            for (int i = 0; i < timing.Count; i++) if (timing[i].Op == key) { t = timing[i]; break; }
            if (t == null) { t = new BatchOpTiming { Op = key }; timing.Add(t); }
            t.Count++;
            t.TotalMs += ms;
            if (ms > t.MaxMs) t.MaxMs = ms;
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

        /// <summary>
        /// A reply that is one number becomes a numeric cell: a bare number ("+5.00000000E+008"), or one in an
        /// older HP instrument's header-and-unit form ("FA 1.5E6 HZ"). Anything else - a list ("005,403,UNJ"),
        /// an error with its message ("+16,\"Input Overload\""), an identity ("HP3325B") - is kept whole. It
        /// used to take the first number found anywhere in the reply, which turned "*OPT?" into 5 and dropped
        /// every error message (#141).
        /// </summary>
        internal static object ParseCell(string response)
        {
            if (response == null) return null;
            string trimmed = response.Trim();
            if (Tools.MeasurementValue.TryParseNumber(trimmed, out double d)) return d;
            var m = HeaderedNumber.Match(trimmed);
            if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out d))
                return d;
            return trimmed;
        }

        /// <summary>One number, optionally after a word header and a space, optionally followed by a unit: "FA 1.5E6 HZ".</summary>
        private static readonly Regex HeaderedNumber = new Regex(
            @"^(?:[A-Za-z]+\s+)?([-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?)\s*[A-Za-z]*$", RegexOptions.Compiled);

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
