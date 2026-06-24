using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using GpibMcp.Diagnostics;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Srq.Completion;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// The batch/sweep tool (#59): Claude authors one compact plan (an optional swept dimension + the
    /// ordered GPIB ops to run at each point, with {{var}}/{{capture}} interpolation), the server expands
    /// and runs the whole thing across the instruments, and returns one structured table - so an N-point
    /// measurement is a single tool call instead of ~N*ops round-trips. The engine is <see cref="BatchRunner"/>;
    /// this layer parses the plan, wires the bus executor, and serialises the result envelope.
    /// </summary>
    public static class BatchTools
    {
        private const string Description =
            "Run a whole multi-step / swept GPIB measurement in ONE call instead of many. Use this whenever " +
            "the user asks for a sweep or a repeated per-point measurement (e.g. 'step the source 1-20 MHz and " +
            "read the analyzer at each point'). Provide a 'sweep' {var, from, to, step|count, unit?} and an " +
            "ordered 'steps' list run at every point. Each step has an 'op': 'set' (value+unit via the resolver: " +
            "{op:'set', resource, command:<name>, value, unit}), 'write' (literal command), 'query'/'read' (read " +
            "a value; add 'as':<name> to capture it into a result column and reuse it as {{name}} in later steps), " +
            "'complete' ({op:'complete', resource, operation:<statusModel op e.g. sweepComplete>} - waits for the " +
            "instrument to truly finish a sweep via SRQ before you read; use it between setting CF and a peak " +
            "search), or 'wait' ({op:'wait', settle_ms}). Interpolate the sweep var and captures with {{name}}. " +
            "'resource' may be a VISA string or an assigned model name. 'on_error':'stop'|'continue'. Returns a " +
            "JSON envelope {ran, columns, rows (array-of-arrays), errors, truncated} plus a ready-to-show 'summary' " +
            "line and a markdown 'table' - relay those to the user. Pass preview:true to get the plan size without " +
            "touching the bus. LARGE plans (more than ~50 GPIB ops) are gated: the call returns needs_confirm with a " +
            "preview and sends NOTHING to the bus - show the user what will run, and only if they approve, call again " +
            "with confirm:true to execute. Small plans run immediately.";

        public static void Register(ToolRegistry registry, InstrumentDatabase db, AssignmentStore assignments,
                                    IInstrumentManager visa)
        {
            registry.Add(new McpTool(
                "gpib_batch", Description,
                Schema(
                    Required("steps", "array", "Ordered steps run at each sweep point. Each: {op:'set'|'write'|" +
                        "'query'|'read'|'complete'|'wait', resource, command?, value?, unit?, as?, operation?, settle_ms?, timeout_ms?}."),
                    Prop("sweep", "object", "Optional swept dimension: {var, from, to, step} or {var, from, to, count} (+ optional unit). " +
                        "Omit to run the steps once."),
                    Prop("on_error", "string", "'stop' (default) or 'continue' (record the error, keep going)."),
                    Prop("preview", "boolean", "If true, return the plan size (points/ops) WITHOUT running anything."),
                    Prop("confirm", "boolean", "Set true to execute a LARGE plan that was gated for confirmation " +
                        "(more than ~50 GPIB ops). Small plans run without it; ignored when preview:true.")),
                (Func<JObject, ToolOutput>)(args =>
                {
                    BatchPlan plan;
                    try { plan = ParsePlan(args); }
                    catch (Exception ex) { return ToolOutput.Text("Invalid batch: " + ex.Message).AsError(); }

                    var caps = new BatchCaps();
                    string invalid = BatchRunner.Validate(plan, caps);
                    if (invalid != null) return ToolOutput.Text("Batch rejected: " + invalid).AsError();

                    var pv = BatchRunner.Preview(plan, caps);

                    // Explicit preview: report the plan size, touch nothing.
                    if (Bool(args, "preview", false))
                        return ToolOutput.Text(PreviewEnvelope(pv, needsConfirm: false,
                            "Nothing was sent to the bus. Call again without preview to execute.").ToString(Formatting.None));

                    // Confirm gate (#59 Phase 2): a large plan returns a preview and runs nothing until the caller
                    // re-submits with confirm:true. Small plans run straight through - no friction.
                    if (pv.GpibOps > caps.ConfirmAboveOps && !Bool(args, "confirm", false))
                        return ToolOutput.Text(PreviewEnvelope(pv, needsConfirm: true,
                            "This is a large batch (" + pv.GpibOps + " GPIB ops over " + pv.Points + " points). Nothing " +
                            "was sent to the bus. Show the user what will run; if they approve, call again with " +
                            "confirm:true to execute.").ToString(Formatting.None));

                    var exec = new BatchExecutor(db, assignments, visa);
                    var watch = Stopwatch.StartNew();
                    BatchResult result = BatchRunner.Run(plan, exec, caps, () => watch.ElapsedMilliseconds);

                    // #58 instrumentation: append the per-op timing breakdown to batch-timing.log so a bench
                    // sweep can be inspected afterwards (where the wall-clock went per op type).
                    BatchTimingLog.Write(result);
                    Log.Info("batch run: " + Summarize(result) + " (timing -> " + BatchTimingLog.Path + ")");

                    return ToolOutput.Text(Serialize(result));
                })));
        }

        // ---- parse the tool args into a plan ------------------------------------

        private static BatchPlan ParsePlan(JObject args)
        {
            var plan = new BatchPlan { OnError = Str(args, "on_error", "stop") };

            if (args["sweep"] is JObject sw)
            {
                plan.Sweep = new BatchSweep
                {
                    Var = (string)sw["var"] ?? "x",
                    From = Dbl(sw["from"]),
                    To = Dbl(sw["to"]),
                    Step = Dbl(sw["step"]),
                    Count = sw["count"] != null ? (int?)(int)sw["count"] : null,
                    Unit = (string)sw["unit"]
                };
            }

            if (!(args["steps"] is JArray steps) || steps.Count == 0)
                throw new Exception("'steps' must be a non-empty array.");
            foreach (var item in steps)
            {
                if (!(item is JObject s)) throw new Exception("each step must be an object.");
                plan.Steps.Add(new BatchStep
                {
                    Op = (string)s["op"],
                    Resource = (string)s["resource"],
                    Command = (string)s["command"],
                    As = (string)s["as"],
                    Value = s["value"] != null ? Convert.ToString(((JValue)s["value"]).Value, CultureInfo.InvariantCulture) : null,
                    Unit = (string)s["unit"],
                    Operation = (string)s["operation"],
                    SettleMs = s["settle_ms"] != null ? (int)s["settle_ms"] : 0,
                    TimeoutMs = s["timeout_ms"] != null ? (int?)(int)s["timeout_ms"] : null
                });
            }
            return plan;
        }

        /// <summary>The preview/confirm envelope: the plan size with nothing sent to the bus.</summary>
        private static JObject PreviewEnvelope(BatchRanInfo pv, bool needsConfirm, string note)
        {
            var jo = new JObject
            {
                ["ok"] = true,
                ["preview"] = true,
                ["ran"] = new JObject { ["sweep"] = pv.Sweep, ["points"] = pv.Points,
                    ["ops_per_point"] = pv.OpsPerPoint, ["gpib_ops"] = pv.GpibOps },
                ["note"] = note
            };
            if (needsConfirm) jo["needs_confirm"] = true;
            return jo;
        }

        private static double Dbl(JToken t) =>
            t == null ? 0 : double.Parse(Convert.ToString(((JValue)t).Value, CultureInfo.InvariantCulture),
                                         NumberStyles.Float, CultureInfo.InvariantCulture);

        // ---- serialise the result envelope (compact, array-of-arrays rows) ------

        private static string Serialize(BatchResult r)
        {
            var jo = new JObject
            {
                ["ok"] = r.Ok,
                ["ran"] = new JObject
                {
                    ["sweep"] = r.Ran.Sweep,
                    ["points"] = r.Ran.Points,
                    ["ops_per_point"] = r.Ran.OpsPerPoint,
                    ["gpib_ops"] = r.Ran.GpibOps,
                    ["elapsed_ms"] = r.Ran.ElapsedMs
                },
                ["columns"] = new JArray(r.Columns.Select(c =>
                {
                    var o = new JObject { ["name"] = c.Name };
                    if (c.Unit != null) o["unit"] = c.Unit;
                    if (c.From != null) o["from"] = c.From;
                    return (JToken)o;
                })),
                ["rows"] = new JArray(r.Rows.Select(row => (JToken)new JArray(row.Select(Cell)))),
                // Ready-to-show renderings (#59 Phase 2): a one-line summary and a markdown table the model can
                // relay to the user verbatim, without having to rebuild them from the array-of-arrays rows.
                ["summary"] = Summarize(r),
                ["table"] = MarkdownTable(r)
            };
            if (r.Errors.Count > 0)
                jo["errors"] = new JArray(r.Errors.Select(e => (JToken)new JObject
                {
                    ["point"] = e.Point, ["step"] = e.Step, ["op"] = e.Op,
                    ["resource"] = e.Resource, ["command"] = e.Command, ["error"] = e.Error
                }));
            if (r.Truncated != null)
                jo["truncated"] = new JObject { ["returned"] = r.Truncated.Returned, ["total"] = r.Truncated.Total, ["reason"] = r.Truncated.Reason };
            return jo.ToString(Formatting.None);
        }

        private static JToken Cell(object v)
        {
            if (v == null) return JValue.CreateNull();
            if (v is double d) return new JValue(d);
            return new JValue(Convert.ToString(v, CultureInfo.InvariantCulture));
        }

        // ---- human-readable renderings (#59 Phase 2) ----------------------------

        /// <summary>A one-line plan/run summary: "39 points, 156 ops, 8.2 s, 0 errors".</summary>
        private static string Summarize(BatchResult r)
        {
            string pts = r.Ran.Points + (r.Ran.Points == 1 ? " point" : " points");
            string secs = (r.Ran.ElapsedMs / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s";
            string errs = r.Errors.Count + (r.Errors.Count == 1 ? " error" : " errors");
            string summary = pts + ", " + r.Ran.GpibOps + " ops, " + secs + ", " + errs;
            if (r.Truncated != null)
                summary += " (table truncated to " + r.Truncated.Returned + " of " + r.Truncated.Total + " rows)";
            return summary;
        }

        /// <summary>Renders the result as a GitHub-flavoured markdown table: header (name + unit), numeric
        /// columns right-aligned. Returns an empty string when there are no columns to show.</summary>
        private static string MarkdownTable(BatchResult r)
        {
            if (r.Columns.Count == 0 || r.Rows.Count == 0) return "";

            // A column is numeric if every non-null cell in it is a number; numeric columns are right-aligned.
            int n = r.Columns.Count;
            var numeric = new bool[n];
            for (int c = 0; c < n; c++)
            {
                bool any = false, allNum = true;
                foreach (var row in r.Rows)
                {
                    object cell = c < row.Count ? row[c] : null;
                    if (cell == null) continue;
                    any = true;
                    if (!(cell is double)) { allNum = false; break; }
                }
                numeric[c] = any && allNum;
            }

            var sb = new StringBuilder();
            sb.Append("| ");
            for (int c = 0; c < n; c++)
            {
                var col = r.Columns[c];
                sb.Append(string.IsNullOrEmpty(col.Unit) ? col.Name : col.Name + " (" + col.Unit + ")");
                sb.Append(c == n - 1 ? " |" : " | ");
            }
            sb.Append('\n').Append("|");
            for (int c = 0; c < n; c++) sb.Append(numeric[c] ? " ---: |" : " --- |");
            foreach (var row in r.Rows)
            {
                sb.Append('\n').Append("| ");
                for (int c = 0; c < n; c++)
                {
                    object cell = c < row.Count ? row[c] : null;
                    string text = cell == null ? ""
                        : cell is double d ? BatchRunner.FormatNumber(d)
                        : Convert.ToString(cell, CultureInfo.InvariantCulture);
                    sb.Append(text);
                    sb.Append(c == n - 1 ? " |" : " | ");
                }
            }
            return sb.ToString();
        }

        // ---- the bus executor ---------------------------------------------------

        /// <summary>Runs batch steps against the real instruments: resolves model-or-VISA resources, applies
        /// per-model terminators, reuses the value resolver for <c>set</c> and the SRQ waiter for <c>complete</c>.</summary>
        private sealed class BatchExecutor : IBatchExecutor
        {
            private readonly InstrumentDatabase _db;
            private readonly AssignmentStore _assignments;
            private readonly IInstrumentManager _visa;

            public BatchExecutor(InstrumentDatabase db, AssignmentStore assignments, IInstrumentManager visa)
            {
                _db = db; _assignments = assignments; _visa = visa;
            }

            /// <summary>Resolves a step's resource (a VISA string or an assigned model name) to a VISA resource; out model name.</summary>
            private string ToVisa(string r, out string model)
            {
                if (string.IsNullOrWhiteSpace(r)) throw new Exception("a step is missing 'resource'.");
                if (r.IndexOf("::", StringComparison.Ordinal) >= 0) { model = _assignments.Get(r); return r; }
                model = r;
                foreach (var kv in _assignments.All())
                    if (string.Equals(kv.Value, r, StringComparison.OrdinalIgnoreCase)) return kv.Key;
                throw new Exception("'" + r + "' is not a VISA resource and no assigned instrument has that model.");
            }

            public void Write(string resource, string command, int timeoutMs)
            {
                string vr = ToVisa(resource, out _);
                _visa.Write(vr, command, InstrumentIo.Resolve(_db, _assignments, vr, timeoutMs));
            }

            public string Query(string resource, string command, int timeoutMs)
            {
                string vr = ToVisa(resource, out _);
                return Clean(_visa.Query(vr, command, InstrumentIo.Resolve(_db, _assignments, vr, timeoutMs)));
            }

            public void Set(string resource, string command, double value, string unit, int timeoutMs)
            {
                string vr = ToVisa(resource, out string model);
                if (string.IsNullOrEmpty(model)) throw new Exception("no model is known for '" + resource + "' to resolve a 'set'.");
                if (!_db.TryGet(model, out InstrumentDefinition def)) throw new Exception("unknown model '" + model + "'.");
                if (!DatabaseTools.TryResolveSettingWire(def, command, value, unit, out string wire, out string err))
                    throw new Exception(err);
                _visa.Write(vr, wire, InstrumentIo.Resolve(_db, _assignments, vr, timeoutMs));
            }

            public void Complete(string resource, string operation, int timeoutMs)
            {
                string vr = ToVisa(resource, out _);
                var res = InstrumentTools.RunCompletion(_db, _assignments, _visa, vr, operation, timeoutMs);
                if (res.Outcome != CompletionOutcome.Completed)
                    throw new Exception(res.Message);
            }

            public void Sleep(int ms) { if (ms > 0) System.Threading.Thread.Sleep(ms); }
        }
    }
}
