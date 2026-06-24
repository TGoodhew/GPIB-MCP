using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
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
            "JSON envelope {ran, columns, rows (array-of-arrays), errors, truncated}. Pass preview:true to get the " +
            "plan size without touching the bus.";

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
                    Prop("preview", "boolean", "If true, return the plan size (points/ops) WITHOUT running anything.")),
                (Func<JObject, ToolOutput>)(args =>
                {
                    BatchPlan plan;
                    try { plan = ParsePlan(args); }
                    catch (Exception ex) { return ToolOutput.Text("Invalid batch: " + ex.Message).AsError(); }

                    var caps = new BatchCaps();
                    string invalid = BatchRunner.Validate(plan, caps);
                    if (invalid != null) return ToolOutput.Text("Batch rejected: " + invalid).AsError();

                    if (Bool(args, "preview", false))
                    {
                        var pv = BatchRunner.Preview(plan, caps);
                        return ToolOutput.Text(new JObject
                        {
                            ["ok"] = true, ["preview"] = true,
                            ["ran"] = new JObject { ["sweep"] = pv.Sweep, ["points"] = pv.Points,
                                ["ops_per_point"] = pv.OpsPerPoint, ["gpib_ops"] = pv.GpibOps },
                            ["note"] = "Nothing was sent to the bus. Call again without preview to execute."
                        }.ToString(Formatting.None));
                    }

                    var exec = new BatchExecutor(db, assignments, visa);
                    var watch = Stopwatch.StartNew();
                    BatchResult result = BatchRunner.Run(plan, exec, caps, () => watch.ElapsedMilliseconds);
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
                ["rows"] = new JArray(r.Rows.Select(row => (JToken)new JArray(row.Select(Cell))))
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
