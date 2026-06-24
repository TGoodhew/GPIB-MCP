using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Srq.Completion;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using static GpibMcp.Tools.ToolArgs;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Builds the registry of MCP tools and binds each one to the instrument layer.
    /// Tool argument shapes are described with JSON Schema so the model knows how to call them.
    /// </summary>
    public static class InstrumentTools
    {
        /// <summary>Convenience overload with an empty database and in-memory assignments (tests).</summary>
        public static ToolRegistry BuildRegistry(IInstrumentManager visa) =>
            BuildRegistry(visa, InstrumentDatabase.Empty(), AssignmentStore.InMemory());

        /// <summary>Creates the registry of all tools the server exposes.</summary>
        public static ToolRegistry BuildRegistry(IInstrumentManager visa, InstrumentDatabase db,
                                                 AssignmentStore assignments)
        {
            if (visa == null) throw new ArgumentNullException(nameof(visa));
            if (db == null) throw new ArgumentNullException(nameof(db));
            if (assignments == null) throw new ArgumentNullException(nameof(assignments));
            var registry = new ToolRegistry();

            // ---- Self-description: detailed capability overview (issue #36) -----
            // Generated on demand from the LIVE registry + database, so it never drifts. The closure
            // captures `registry`, which is fully populated by the time the tool is called.
            registry.Add(new McpTool(
                "gpib_overview",
                "Describe what this GPIB/VISA server can do, in detail: capability areas (discovery, I/O, " +
                "identify/assign, the instrument command database, screen capture, SRQ completion, error " +
                "reporting, configuration), example phrasings, and a complete list of the available tools. " +
                "Call this to answer 'what can the GPIB tool do?' or 'what instruments/commands are supported?' " +
                "instead of guessing from individual tool descriptions.",
                Schema(),
                args => new ServerOverview(registry, db).Detailed()));

            // ---- VISA: discovery -------------------------------------------------
            registry.Add(new McpTool(
                "visa_list_resources",
                "List connected VISA instrument resources (GPIB, USB, TCPIP/LXI, serial). " +
                "Returns resource strings such as 'GPIB0::5::INSTR' or 'TCPIP0::192.168.0.10::INSTR'.",
                Schema(
                    Prop("filter", "string", "Optional VISA resource regex, e.g. 'GPIB?*INSTR'. Defaults to '?*INSTR'.")),
                args =>
                {
                    string filter = Str(args, "filter", null);
                    var resources = visa.ListResources(filter);
                    if (resources.Count == 0) return "No VISA resources found.";
                    var sb = new StringBuilder();
                    sb.AppendLine("Found " + resources.Count + " VISA resource(s):");
                    foreach (var r in resources) sb.AppendLine("  " + r);

                    int gpibCount = CountGpibInstruments(resources);
                    if (gpibCount >= PhantomGpibThreshold())
                        sb.Append(Environment.NewLine + BusExtenderAdvisory(gpibCount));
                    return sb.ToString().TrimEnd();
                }));

            // ---- VISA: query (write + read) -------------------------------------
            registry.Add(new McpTool(
                "visa_query",
                "Send a command to a VISA instrument and return its response. " +
                "Use for SCPI queries ending in '?', e.g. '*IDN?' or 'MEAS:VOLT:DC?'. " +
                "Uses the assigned model's read/write terminators automatically. If a FREE-RUNNING " +
                "instrument (one that streams output continuously) makes a query time out, pass " +
                "read_bytes to cap the read so it returns promptly.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::5::INSTR'."),
                    Required("command", "string", "Command/query to send. A newline terminator is added if absent."),
                    Prop("timeout_ms", "integer", "I/O timeout in milliseconds (default 5000)."),
                    Prop("read_bytes", "integer", "Optional: read at most this many bytes instead of reading to the " +
                        "terminator/EOI. Leave unset for normal reads; use it only to stop a free-running instrument " +
                        "from timing out (e.g. 512 for an identity read).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    string command = ReqStr(args, "command");
                    int timeout = Int(args, "timeout_ms", InstrumentManager.DefaultTimeoutMs);
                    int readBytes = Int(args, "read_bytes", 0);
                    var io = InstrumentIo.Resolve(db, assignments, resource, timeout, readBytes);
                    return Clean(visa.Query(resource, command, io));
                }));

            // ---- VISA: write-only ------------------------------------------------
            registry.Add(new McpTool(
                "visa_write",
                "Send a command to a VISA instrument with no expected response, e.g. 'OUTP ON' or '*RST'.",
                Schema(
                    Required("resource", "string", "VISA resource string."),
                    Required("command", "string", "Command to send."),
                    Prop("timeout_ms", "integer", "I/O timeout in milliseconds (default 5000).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    string command = ReqStr(args, "command");
                    int timeout = Int(args, "timeout_ms", InstrumentManager.DefaultTimeoutMs);
                    visa.Write(resource, command, timeout);
                    return "OK (wrote: " + command + ")";
                }));

            // ---- VISA: read pending ---------------------------------------------
            registry.Add(new McpTool(
                "visa_read",
                "Read a pending response from a VISA instrument that was previously written to. " +
                "Uses the assigned model's read terminator automatically; pass read_bytes to cap the " +
                "read for a free-running instrument that would otherwise time out.",
                Schema(
                    Required("resource", "string", "VISA resource string."),
                    Prop("timeout_ms", "integer", "I/O timeout in milliseconds (default 5000)."),
                    Prop("read_bytes", "integer", "Optional: read at most this many bytes instead of reading to the " +
                        "terminator/EOI. Use only to stop a free-running instrument from timing out.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int timeout = Int(args, "timeout_ms", InstrumentManager.DefaultTimeoutMs);
                    int readBytes = Int(args, "read_bytes", 0);
                    var io = InstrumentIo.Resolve(db, assignments, resource, timeout, readBytes);
                    return Clean(visa.Read(resource, io));
                }));

            // ---- VISA: identify (convenience) -----------------------------------
            registry.Add(new McpTool(
                "visa_identify",
                "Convenience helper: query '*IDN?' and return the instrument identification string. " +
                "Pass read_bytes if a free-running instrument makes the identity read time out.",
                Schema(
                    Required("resource", "string", "VISA resource string."),
                    Prop("read_bytes", "integer", "Optional: read at most this many bytes (e.g. 512) so a free-running " +
                        "instrument's identity read returns promptly instead of timing out.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int readBytes = Int(args, "read_bytes", 0);
                    var io = InstrumentIo.Resolve(db, assignments, resource, InstrumentManager.DefaultTimeoutMs, readBytes);
                    return Clean(visa.Query(resource, "*IDN?", io));
                }));

            // ---- VISA: device clear ---------------------------------------------
            registry.Add(new McpTool(
                "visa_clear",
                "Send an IEEE 488.2 device clear to reset the instrument's I/O state (clears the GPIB " +
                "input/output buffers). CAUTION: on some instruments - notably HP 8560-series spectrum " +
                "analyzers - a device clear ALSO executes a full instrument PRESET, wiping the current " +
                "settings (center freq, span, ref level). Don't device-clear those unless you intend to reset them.",
                Schema(
                    Required("resource", "string", "VISA resource string.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    visa.Clear(resource, InstrumentManager.DefaultTimeoutMs);
                    return "OK (device cleared)";
                }));

            // ---- VISA: session management ---------------------------------------
            registry.Add(new McpTool(
                "visa_list_open",
                "List VISA resources currently held open by this server.",
                Schema(),
                args =>
                {
                    var open = visa.ListOpen();
                    if (open.Count == 0) return "No open sessions.";
                    return "Open sessions:\n  " + string.Join("\n  ", open);
                }));

            registry.Add(new McpTool(
                "visa_close",
                "Close a VISA session held open by this server (frees the instrument).",
                Schema(
                    Required("resource", "string", "VISA resource string.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    return visa.Close(resource) ? "Closed " + resource : "No open session for " + resource;
                }));

            // ---- Diagnostics: recent command chain ------------------------------
            registry.Add(new McpTool(
                "visa_command_history",
                "Show the recent commands this server sent to / received from an instrument - the " +
                "chain leading up to now (or to an error). Useful for diagnosing what a tool actually did.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::5::INSTR'."),
                    Prop("max", "integer", "Maximum number of recent entries to return (default 20).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int max = Int(args, "max", CommandHistory.DefaultDepth);
                    var history = visa.RecentCommands(resource, max);
                    if (history.Count == 0) return "No command history for " + resource + ".";
                    var sb = new StringBuilder();
                    sb.AppendLine("Recent commands for " + resource + " (-> sent / <- received):");
                    foreach (var entry in history) sb.AppendLine("  " + entry.ToLine());
                    return sb.ToString().TrimEnd();
                }));

            // ---- Diagnostics: exact last error ----------------------------------
            registry.Add(new McpTool(
                "visa_last_error",
                "Return the EXACT, verbatim details of the most recent GPIB/VISA failure: the decoded " +
                "VISA status name, the raw numeric status code (hex + decimal), the meaning, the " +
                "underlying driver exception text, the timestamp, and the command chain. Use this when " +
                "the user asks for the precise/exact error codes and text (the normal tool error is a " +
                "friendlier summary). Pass a resource for that instrument's last error, or omit for the " +
                "most recent error on any resource.",
                Schema(
                    Prop("resource", "string", "Optional VISA resource string; omit for the most recent error on any resource.")),
                args =>
                {
                    string resource = Str(args, "resource", null);
                    var error = visa.LastError(resource);
                    if (error != null) return error.VerboseDetail;
                    return string.IsNullOrEmpty(resource)
                        ? "No GPIB/VISA errors have been recorded this session."
                        : "No recent error recorded for " + resource + ".";
                }));

            // ---- SRQ / status: serial poll --------------------------------------
            registry.Add(new McpTool(
                "visa_serial_poll",
                "Serial-poll a GPIB instrument and return its status byte (decimal + hex). When the " +
                "resource is assigned a model that has a statusModel, also lists the named status bits " +
                "that are set; otherwise lists the standard IEEE 488.2 bits (RQS/ESB/MAV).",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int stb = visa.SerialPoll(resource);

                    var sb = new StringBuilder();
                    sb.AppendLine("Status byte for " + resource + ": " + stb + " (0x" + stb.ToString("X2") + ")");

                    StatusModel model = ResolveStatusModel(db, assignments, resource);
                    if (model != null && model.Bits != null)
                    {
                        var named = model.SetBitNames(stb);
                        sb.AppendLine(named.Count > 0 ? "  bits set: " + string.Join(", ", named) : "  bits set: (none)");
                        if (model.SerialPoll != null)
                            sb.AppendLine("  serial poll " + (model.SerialPoll.ClearsRqs ? "clears" : "does not clear") + " RQS");
                    }
                    else
                    {
                        // No model: fall back to the standard IEEE 488.2 bits (note: some legacy
                        // instruments such as the 8560 series use a non-standard status byte).
                        var std = StandardStatusBits(stb);
                        sb.AppendLine(std.Count > 0
                            ? "  standard bits: " + string.Join(", ", std) + "  (no statusModel; assign a model for an instrument-specific decode)"
                            : "  no standard bits set (no statusModel for this resource)");
                    }
                    return sb.ToString().TrimEnd();
                }));

            // ---- SRQ / status: wait for SRQ -------------------------------------
            registry.Add(new McpTool(
                "visa_wait_srq",
                "Block until the instrument asserts SRQ (service request) on the GPIB bus, or the " +
                "backstop timeout expires. Pure mechanism: it does NOT serial-poll (call " +
                "visa_serial_poll afterward to read/clear the status byte). Returns whether SRQ " +
                "asserted and the elapsed time.",
                Schema(
                    Required("resource", "string", "VISA resource string."),
                    Prop("timeout_ms", "integer", "Backstop timeout in milliseconds (default 5000).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int timeout = Int(args, "timeout_ms", InstrumentManager.DefaultTimeoutMs);
                    var result = visa.WaitForSrq(resource, timeout);
                    return result.Asserted
                        ? "SRQ asserted on " + resource + " after " + result.ElapsedMs + " ms."
                        : "No SRQ on " + resource + " within " + timeout + " ms (waited " + result.ElapsedMs + " ms).";
                }));

            // ---- SRQ: high-level, data-driven completion waiter -----------------
            registry.Add(new McpTool(
                "instrument_wait_complete",
                "Wait for an instrument operation to ACTUALLY complete via SRQ, driven by the model's " +
                "statusModel - no fixed-timeout guessing. Resolves the model from the resource's " +
                "assignment, arms the operation's SRQ mask, waits for SRQ, serial-polls to confirm the " +
                "expected status bit, and clears the mask. Refuses if the model declares no SRQ support. " +
                "If the statusModel/operation is missing it asks for the definitions (never guesses); " +
                "supply them via status_model to define-and-persist in one step (proposes first, writes " +
                "on confirm=true, then proceeds with the wait).",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::18::INSTR'."),
                    Required("operation", "string", "Operation name from the model's statusModel.operations (e.g. 'sweepComplete')."),
                    Prop("timeout_ms", "integer", "Backstop timeout in ms (default 30000) - a safety net, NOT the completion signal."),
                    Prop("status_model", "object", "Optional statusModel block to define/extend for this model when it is " +
                        "missing or incomplete (merged over any existing one): { srqSupported, bits:{name:weight}, " +
                        "enableMask:{setCommand,clearCommand}, errorBit, requestServiceBit, serialPoll:{clearsRqs}, " +
                        "operations:{<name>:{arm,expectBit,restore}} }. Proposed first; persisted to the model's user " +
                        "DB record only with confirm=true, after which the wait proceeds."),
                    Prop("confirm", "boolean", "Set true to persist the supplied status_model before waiting (default false = propose only).")),
                (Func<JObject, ToolOutput>)(args =>
                    WaitComplete(db, assignments, visa,
                                 ReqStr(args, "resource"), ReqStr(args, "operation"), Int(args, "timeout_ms", 30000),
                                 args["status_model"] as JObject, Bool(args, "confirm", false)))));

            // ---- Native GPIB query (board/primary/secondary) --------------------
            registry.Add(new McpTool(
                "gpib488_query",
                "Query a GPIB instrument directly by board/primary/secondary address instead of a VISA " +
                "resource string. Requires a backend with native GPIB addressing (the default NI-VISA/" +
                "NI-488.2 backend has it; some adapters do not).",
                Schema(
                    Required("primary_address", "integer", "GPIB primary address (0-30)."),
                    Prop("board", "integer", "GPIB board/controller index (default 0)."),
                    Prop("secondary_address", "integer", "GPIB secondary address (96-126), or 0 for none (default 0)."),
                    Required("command", "string", "Command/query to send. A newline terminator is added if absent.")),
                args =>
                {
                    if (!visa.Capabilities.NativeAddressing)
                        return "The active GPIB backend (" + visa.Capabilities.Name + ") does not support native " +
                               "board/primary/secondary addressing. Use visa_query with a resource string instead.";
                    int board = Int(args, "board", 0);
                    byte primary = (byte)Int(args, "primary_address", -1, 0, 30, "primary_address");
                    byte secondary = (byte)Int(args, "secondary_address", InstrumentManager.NoSecondaryAddress);
                    string command = ReqStr(args, "command");
                    return Clean(visa.NativeQuery(board, primary, secondary, command));
                }));

            // ---- Instrument command database + assignments ----------------------
            DatabaseTools.Register(registry, db, assignments, visa);

            // ---- Screen capture (HP-GL plotter emulation -> image) --------------
            CaptureTools.Register(registry, db, assignments, visa);

            // ---- Batch / sweep execution (#59): one call runs a whole sweep -----
            BatchTools.Register(registry, db, assignments, visa);

            return registry;
        }

        // ---------------------------------------------------------------------
        // Bus-extender (phantom address) detection
        //
        // Bus-level GPIB discovery reports an address as "present" whenever a
        // listener acknowledges on the bus. HPIB bus extenders (e.g. HP 37204A)
        // acknowledge EVERY address, so discovery returns a phantom-full bus and
        // the result cannot be trusted. A physical GPIB segment supports at most
        // ~15 devices, so a count at/above that limit is the tell-tale sign.
        // ---------------------------------------------------------------------

        // ---------------------------------------------------------------------
        // instrument_wait_complete - thin adapter over the CompletionWaiter component.
        // The dispatch + flow logic lives in GpibMcp.Instruments.Completion (decoupled
        // from VISA and headlessly testable); this only resolves the model and bridges I/O.
        // ---------------------------------------------------------------------

        private static ToolOutput WaitComplete(InstrumentDatabase db, AssignmentStore assignments,
                                               IInstrumentManager visa, string resource, string operation, int timeoutMs,
                                               JObject statusModelPatch, bool confirm)
        {
            string model = assignments.Get(resource);
            if (string.IsNullOrEmpty(model))
                return Err("No model is assigned to " + resource +
                           ". Assign one with assign_instrument so its statusModel can be resolved.");

            InstrumentDefinition def;
            if (!db.TryGet(model, out def))
                return Err("Unknown model '" + model + "' assigned to " + resource + ".");

            // #18: optional inline statusModel definition (prompt-and-persist). Proposes on the first
            // call; on confirm=true it persists to the model's user-DB record, then falls through to wait.
            string savedNote = null;
            if (statusModelPatch != null)
            {
                ToolOutput proposalOrError = DefineStatusModel(db, def, model, operation, statusModelPatch, confirm, out savedNote);
                if (proposalOrError != null) return proposalOrError;  // unconfirmed proposal, or a validation/save error
            }

            var channel = new VisaStatusChannel(visa, resource);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            CompletionResult result;
            try
            {
                result = CompletionWaiter.Wait(def.StatusModel, model, operation, timeoutMs, channel,
                                               () => watch.ElapsedMilliseconds, System.Threading.Thread.Sleep);
            }
            catch (GpibOperationException gex) { return Err(gex.Detail); }
            catch (Exception ex) { return Err("Wait failed for " + resource + ": " + ex.Message); }

            string prefix = savedNote != null ? savedNote + "\n" : "";
            switch (result.Outcome)
            {
                case CompletionOutcome.Completed:
                    return Txt(prefix + "[" + resource + "] " + result.Message);
                case CompletionOutcome.InstrumentError:
                case CompletionOutcome.TimedOut:
                    return Err(prefix + "[" + resource + "] " + result.Message);
                case CompletionOutcome.Refused:
                    return Err(result.Message);
                case CompletionOutcome.NeedsDefinition:
                default:
                    return Txt(result.Message + DefineHint(operation));
            }
        }

        /// <summary>
        /// Runs the model's statusModel completion wait for a resource and returns the raw result (the
        /// shared core of <c>instrument_wait_complete</c>, reused by the batch <c>complete</c> op, #59):
        /// resolves the model from the assignment, builds the serial-poll channel, and waits via the
        /// data-driven <see cref="CompletionWaiter"/>.
        /// </summary>
        internal static CompletionResult RunCompletion(InstrumentDatabase db, AssignmentStore assignments,
                                                       IInstrumentManager visa, string resource, string operation, int timeoutMs)
        {
            string model = assignments.Get(resource);
            StatusModel sm = null;
            InstrumentDefinition def;
            if (!string.IsNullOrEmpty(model) && db.TryGet(model, out def)) sm = def.StatusModel;
            var channel = new VisaStatusChannel(visa, resource);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            return CompletionWaiter.Wait(sm, model ?? resource, operation, timeoutMs, channel,
                                         () => watch.ElapsedMilliseconds, System.Threading.Thread.Sleep);
        }

        /// <summary>Tool affordance appended to a NeedsDefinition prompt: the one-step define-and-persist path.</summary>
        private static string DefineHint(string operation) =>
            "\n\nOne-step option: call instrument_wait_complete again with operation='" + operation + "' and " +
            "status_model set to that block. It proposes the save first; on confirm=true it persists the " +
            "statusModel to the model's user-DB record and then waits - no separate save step needed.";

        // ---------------------------------------------------------------------
        // #18: define-and-persist a statusModel supplied to the tool. Merges the block over any existing
        // statusModel (camelCase, matching the bundled JSON so keys align and the file stays consistent),
        // validates it, and on confirm writes a minimal user-DB override (model + statusModel) and makes it
        // effective immediately. Returns a ToolOutput to send back (an unconfirmed proposal, or an error),
        // or null once persisted - in which case the caller proceeds to the wait with the merged definition.
        // ---------------------------------------------------------------------
        private static readonly JsonSerializerSettings CamelStatusSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore
        };

        private static ToolOutput DefineStatusModel(InstrumentDatabase db, InstrumentDefinition def, string model,
                                                    string operation, JObject patch, bool confirm, out string savedNote)
        {
            savedNote = null;

            JObject current = def.StatusModel != null
                ? JObject.FromObject(def.StatusModel, JsonSerializer.Create(CamelStatusSettings))
                : new JObject();
            var merged = (JObject)current.DeepClone();
            merged.Merge(patch, new JsonMergeSettings
            {
                MergeArrayHandling = MergeArrayHandling.Replace,
                MergeNullValueHandling = MergeNullValueHandling.Ignore
            });

            StatusModel parsed;
            try { parsed = merged.ToObject<StatusModel>(); }
            catch (Exception ex) { return Err("Invalid status_model for '" + model + "': " + ex.Message); }
            if (parsed == null) return Err("status_model for '" + model + "' did not parse to a statusModel.");

            string dir = InstrumentPaths.UserDatabaseDir();
            string file = Path.Combine(dir, SanitizeModelFileName(model) + ".json");
            bool exists = File.Exists(file);

            if (!confirm)
                return Txt("PROPOSED statusModel for '" + model + "' (would " + (exists ? "update" : "create") +
                           " " + file + "):\n\n" + merged.ToString(Formatting.Indented) +
                           "\n\nASSISTANT: confirm with the user, then call instrument_wait_complete again with the " +
                           "same status_model and confirm=true to persist it and wait for '" + operation + "'.");

            try
            {
                Directory.CreateDirectory(dir);
                JObject userJson = exists ? JObject.Parse(File.ReadAllText(file)) : new JObject();
                if (userJson["model"] == null) userJson["model"] = model;   // minimal override; bundled blocks merge on load
                userJson["statusModel"] = merged;
                File.WriteAllText(file, userJson.ToString(Formatting.Indented));
            }
            catch (Exception ex) { return Err("Failed to save statusModel for '" + model + "': " + ex.Message); }

            def.StatusModel = parsed;   // effective for this call and subsequent ones
            db.Upsert(def);
            savedNote = "Saved statusModel for '" + model + "' to " + file + ".";
            return null;                // persisted - caller proceeds to the wait
        }

        private static string SanitizeModelFileName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static ToolOutput Err(string message) => ToolOutput.Text(message).AsError();
        private static ToolOutput Txt(string message) => ToolOutput.Text(message);

        /// <summary>Bridges the decoupled <see cref="IStatusChannel"/> to the instrument manager.</summary>
        private sealed class VisaStatusChannel : IStatusChannel
        {
            private readonly IInstrumentManager _visa;
            private readonly string _resource;
            public VisaStatusChannel(IInstrumentManager visa, string resource) { _visa = visa; _resource = resource; }
            public void Send(string command) => _visa.Write(_resource, command, InstrumentManager.DefaultTimeoutMs);
            public int SerialPoll() => _visa.SerialPoll(_resource);
        }

        /// <summary>Resolves the statusModel for a resource via its assignment, or null if none is known.</summary>
        private static StatusModel ResolveStatusModel(InstrumentDatabase db, AssignmentStore assignments, string resource)
        {
            string model = assignments.Get(resource);
            if (string.IsNullOrEmpty(model)) return null;
            InstrumentDefinition def;
            return db.TryGet(model, out def) ? def.StatusModel : null;
        }

        /// <summary>Standard IEEE 488.2 status-byte bits, for instruments without a statusModel.</summary>
        private static System.Collections.Generic.List<string> StandardStatusBits(int stb)
        {
            var list = new System.Collections.Generic.List<string>();
            if ((stb & 0x40) != 0) list.Add("RQS(0x40)");
            if ((stb & 0x20) != 0) list.Add("ESB(0x20)");
            if ((stb & 0x10) != 0) list.Add("MAV(0x10)");
            return list;
        }

        private const int DefaultPhantomGpibThreshold = 15;

        private static int PhantomGpibThreshold()
        {
            string raw = Environment.GetEnvironmentVariable("GPIB_MCP_PHANTOM_GPIB_THRESHOLD");
            int parsed;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out parsed) && parsed > 0)
                return parsed;
            return DefaultPhantomGpibThreshold;
        }

        private static int CountGpibInstruments(IEnumerable<string> resources)
        {
            int count = 0;
            foreach (var r in resources)
            {
                if (r == null) continue;
                if (r.IndexOf("GPIB", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    r.IndexOf("INSTR", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    count++;
                }
            }
            return count;
        }

        private static string BusExtenderAdvisory(int gpibCount)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WARNING: " + gpibCount + " GPIB addresses report as present. A physical GPIB");
            sb.AppendLine("segment supports at most ~15 devices, so this almost always means an HPIB bus");
            sb.AppendLine("extender (e.g. HP 37204A) is on the bus. Such extenders acknowledge EVERY");
            sb.AppendLine("address whether or not an instrument is connected, so the GPIB entries above");
            sb.AppendLine("cannot be trusted - most are phantom addresses.");
            sb.AppendLine();
            sb.AppendLine("ASSISTANT: Before using any GPIB address, ask the user:");
            sb.AppendLine("  1. Are you using an HP 37204A (or similar) HPIB bus extender?");
            sb.AppendLine("  2. If so, which GPIB addresses are actually in use?");
            sb.AppendLine("Then work only with the addresses the user confirms (run visa_identify on each");
            sb.AppendLine("to verify). Non-GPIB resources (USB / TCPIP / serial) listed above are unaffected.");
            return sb.ToString().TrimEnd();
        }
    }
}
