using System;
using System.Collections.Generic;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
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
                "Use for SCPI queries ending in '?', e.g. '*IDN?' or 'MEAS:VOLT:DC?'.",
                Schema(
                    Required("resource", "string", "VISA resource string, e.g. 'GPIB0::5::INSTR'."),
                    Required("command", "string", "Command/query to send. A newline terminator is added if absent."),
                    Prop("timeout_ms", "integer", "I/O timeout in milliseconds (default 5000).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    string command = ReqStr(args, "command");
                    int timeout = Int(args, "timeout_ms", VisaInstrumentManager.DefaultTimeoutMs);
                    return Clean(visa.Query(resource, command, timeout));
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
                    int timeout = Int(args, "timeout_ms", VisaInstrumentManager.DefaultTimeoutMs);
                    visa.Write(resource, command, timeout);
                    return "OK (wrote: " + command + ")";
                }));

            // ---- VISA: read pending ---------------------------------------------
            registry.Add(new McpTool(
                "visa_read",
                "Read a pending response from a VISA instrument that was previously written to.",
                Schema(
                    Required("resource", "string", "VISA resource string."),
                    Prop("timeout_ms", "integer", "I/O timeout in milliseconds (default 5000).")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    int timeout = Int(args, "timeout_ms", VisaInstrumentManager.DefaultTimeoutMs);
                    return Clean(visa.Read(resource, timeout));
                }));

            // ---- VISA: identify (convenience) -----------------------------------
            registry.Add(new McpTool(
                "visa_identify",
                "Convenience helper: query '*IDN?' and return the instrument identification string.",
                Schema(
                    Required("resource", "string", "VISA resource string.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    return Clean(visa.Query(resource, "*IDN?", VisaInstrumentManager.DefaultTimeoutMs));
                }));

            // ---- VISA: device clear ---------------------------------------------
            registry.Add(new McpTool(
                "visa_clear",
                "Send an IEEE 488.2 device clear to reset the instrument's I/O state.",
                Schema(
                    Required("resource", "string", "VISA resource string.")),
                args =>
                {
                    string resource = ReqStr(args, "resource");
                    visa.Clear(resource, VisaInstrumentManager.DefaultTimeoutMs);
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
                    int timeout = Int(args, "timeout_ms", VisaInstrumentManager.DefaultTimeoutMs);
                    var result = visa.WaitForSrq(resource, timeout);
                    return result.Asserted
                        ? "SRQ asserted on " + resource + " after " + result.ElapsedMs + " ms."
                        : "No SRQ on " + resource + " within " + timeout + " ms (waited " + result.ElapsedMs + " ms).";
                }));

            // ---- NI-488.2: direct GPIB query ------------------------------------
            registry.Add(new McpTool(
                "gpib488_query",
                "Query a GPIB instrument directly via the native NI-488.2 driver, addressing it by " +
                "board/primary/secondary instead of a VISA resource string.",
                Schema(
                    Required("primary_address", "integer", "GPIB primary address (0-30)."),
                    Prop("board", "integer", "GPIB board/controller index (default 0)."),
                    Prop("secondary_address", "integer", "GPIB secondary address (96-126), or 0 for none (default 0)."),
                    Required("command", "string", "Command/query to send. A newline terminator is added if absent.")),
                args =>
                {
                    int board = Int(args, "board", 0);
                    byte primary = (byte)Int(args, "primary_address", -1, 0, 30, "primary_address");
                    byte secondary = (byte)Int(args, "secondary_address", Gpib488Helper.NoSecondaryAddress);
                    string command = ReqStr(args, "command");
                    try
                    {
                        return Clean(Gpib488Helper.Query(board, primary, secondary, command));
                    }
                    catch (GpibOperationException gex)
                    {
                        visa.RecordError(gex); // make it retrievable via visa_last_error too
                        throw;
                    }
                }));

            // ---- Instrument command database + assignments ----------------------
            DatabaseTools.Register(registry, db, assignments, visa);

            // ---- Screen capture (HP-GL plotter emulation -> image) --------------
            CaptureTools.Register(registry, db, assignments, visa);

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
