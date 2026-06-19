using System;
using System.Collections.Generic;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Builds the registry of MCP tools and binds each one to the instrument layer.
    /// Tool argument shapes are described with JSON Schema so the model knows how to call them.
    /// </summary>
    public static class InstrumentTools
    {
        /// <summary>Creates the registry of all tools the server exposes, bound to <paramref name="visa"/>.</summary>
        public static ToolRegistry BuildRegistry(VisaInstrumentManager visa)
        {
            if (visa == null) throw new ArgumentNullException(nameof(visa));
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
                    return Clean(Gpib488Helper.Query(board, primary, secondary, command));
                }));

            return registry;
        }

        // ---------------------------------------------------------------------
        // JSON Schema builders
        // ---------------------------------------------------------------------

        private static JObject Schema(params JProperty[] properties)
        {
            var props = new JObject();
            var required = new JArray();
            foreach (var p in properties)
            {
                props.Add(p);
                var meta = (JObject)p.Value;
                if (meta["__required"] != null && meta["__required"].Value<bool>())
                {
                    required.Add(p.Name);
                    meta.Remove("__required");
                }
            }
            var schema = new JObject
            {
                ["type"] = "object",
                ["properties"] = props
            };
            if (required.Count > 0) schema["required"] = required;
            return schema;
        }

        private static JProperty Prop(string name, string type, string description)
        {
            return new JProperty(name, new JObject { ["type"] = type, ["description"] = description });
        }

        private static JProperty Required(string name, string type, string description)
        {
            return new JProperty(name, new JObject
            {
                ["type"] = type,
                ["description"] = description,
                ["__required"] = true
            });
        }

        // ---------------------------------------------------------------------
        // Argument readers
        // ---------------------------------------------------------------------

        private static string Str(JObject args, string key, string fallback)
        {
            var token = args[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<string>();
        }

        private static string ReqStr(JObject args, string key)
        {
            string value = Str(args, key, null);
            if (string.IsNullOrEmpty(value))
                throw new ArgumentException("missing required argument '" + key + "'");
            return value;
        }

        private static int Int(JObject args, string key, int fallback)
        {
            var token = args[key];
            return token == null || token.Type == JTokenType.Null ? fallback : token.Value<int>();
        }

        private static int Int(JObject args, string key, int fallback, int min, int max, string label)
        {
            var token = args[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                if (fallback < min) throw new ArgumentException("missing required argument '" + label + "'");
                return fallback;
            }
            int value = token.Value<int>();
            if (value < min || value > max)
                throw new ArgumentException(label + " must be between " + min + " and " + max);
            return value;
        }

        /// <summary>Trims trailing CR/LF that instruments append to responses.</summary>
        private static string Clean(string response)
        {
            return (response ?? string.Empty).TrimEnd('\r', '\n');
        }
    }
}
