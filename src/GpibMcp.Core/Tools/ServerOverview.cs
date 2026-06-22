using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Generates the server's self-description (issue #36): a concise always-on summary for the
    /// <c>initialize</c> result's <c>instructions</c> field, and a detailed, structured rundown
    /// returned on demand by the <c>gpib_overview</c> tool.
    ///
    /// Both are derived from the LIVE tool registry and instrument database - the headline counts
    /// (tools, models, commands, categories) are read at generation time, not hard-coded, so the
    /// overview stays accurate as tools and data change. The curated capability sections explain the
    /// "why" and give example phrasings; an auto-generated complete tool list guarantees every
    /// registered tool is represented even if a curated section is not updated (a test enforces this).
    /// </summary>
    public sealed class ServerOverview
    {
        private readonly ToolRegistry _tools;
        private readonly InstrumentDatabase _db;

        public ServerOverview(ToolRegistry tools, InstrumentDatabase db)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        private int ToolCount => _tools.Count;
        private int ModelCount => _db.All.Count;
        private int CommandCount => _db.All.Sum(d => d.Commands != null ? d.Commands.Count : 0);
        private int CategoryCount => _db.All
            .Select(d => d.Category)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        /// <summary>One-line headline stats, e.g. "25 tools, 165 instrument models (~6,400 commands across 18 categories)".</summary>
        private string Stats() =>
            ToolCount + " tools, " + ModelCount + " instrument models (" +
            CommandCount.ToString("N0") + " documented commands across " + CategoryCount + " categories)";

        /// <summary>
        /// Concise capability summary for the always-loaded <c>initialize.instructions</c> field.
        /// Kept short - it is in context for the whole session; the depth lives in <see cref="Detailed"/>.
        /// </summary>
        public string Instructions()
        {
            var sb = new StringBuilder();
            sb.AppendLine("gpib-mcp controls GPIB / VISA test-and-measurement instruments (spectrum analyzers, " +
                          "counters, DMMs, signal generators, power supplies, switches) over NI-VISA / NI-488.2.");
            sb.AppendLine("Capabilities: discover instruments on the bus; query/write/read with per-model " +
                          "terminators; identify and assign a model to a resource; look up commands from a " +
                          "built-in database (" + Stats() + "); capture an instrument's screen as an inline SVG " +
                          "(HP-GL emulation); wait for an operation to truly finish via SRQ; and report exact " +
                          "decoded VISA errors with a command history.");
            sb.AppendLine("When the user asks what this tool can do, or which instruments/commands are supported, " +
                          "call the gpib_overview tool for a detailed, structured answer with example asks rather " +
                          "than guessing from individual tool descriptions.");
            sb.Append("Notes: 32-bit (x86) and the NI driver must be installed; GPIB bus extenders " +
                      "(e.g. HP 37204A) make every address look occupied - the server flags this.");
            return sb.ToString();
        }

        /// <summary>
        /// Detailed, well-organised capability rundown for the <c>gpib_overview</c> tool: grouped by area
        /// with example user phrasings, followed by a complete auto-generated list of every registered tool.
        /// </summary>
        public string Detailed()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# GPIB-MCP server overview");
            sb.AppendLine();
            sb.AppendLine("Controls GPIB / VISA test-and-measurement instruments from natural language. " + Stats() + ".");
            sb.AppendLine("Backend: NI-VISA / NI-488.2 by default (set GPIB_MCP_BACKEND to choose another when available). " +
                          "Runs 32-bit (x86); the NI driver must be installed.");
            sb.AppendLine();

            Section(sb, "Discovery",
                "List the VISA resources connected to the PC - GPIB, USB-TMC, TCPIP/LXI and serial. Detects an HPIB " +
                "bus extender (e.g. HP 37204A): such extenders acknowledge every address, so a phantom-full bus is " +
                "flagged rather than trusted.",
                new[] { "\"What instruments are connected?\"", "\"List the GPIB devices on the bus.\"" });

            Section(sb, "I/O (query / write / read / clear)",
                "Send a query and read the reply, write a command with no reply, read a pending response, or send a " +
                "device clear. Read/write terminators come from the assigned model automatically; a bounded read " +
                "(read_bytes) stops a free-running instrument from timing out. Sessions are cached and can be listed/closed.",
                new[] { "\"Ask GPIB0::18 for its center frequency.\"", "\"Send *RST to the DMM.\"",
                        "\"Read whatever the counter is sending.\"" });

            Section(sb, "Identity & assignment",
                "Query *IDN? (or a model's identity command), match the answer against the database, and assign a model " +
                "to a resource so later calls use its terminators, commands and statusModel. Assignment is confirm-to-save. " +
                "Instruments with no remote identification are reported as such instead of guessed.",
                new[] { "\"What is the instrument at GPIB0::5?\"", "\"Assign the 8563E to GPIB0::18.\"" });

            Section(sb, "Instrument command database",
                "A built-in reference of " + ModelCount + " models and " + CommandCount.ToString("N0") + " documented " +
                "commands. Browse the catalogue, look up a model's commands (with mnemonics, set/query forms, parameters " +
                "and examples), and extend it: add or refresh a model and override bundled entries with your own (user " +
                "copies win and persist).",
                new[] { "\"Show the commands for an HP 3458A.\"", "\"List the spectrum analyzers you know.\"",
                        "\"How do I set the resolution bandwidth on the 8563E?\"" });

            Section(sb, "Screen capture",
                "Capture an instrument's screen by HP-GL plotter emulation and return it as a compact inline SVG (shown " +
                "in the chat as an artifact) plus a saved PNG. Fidelity is selectable (exact stroke font vs. fast labels).",
                new[] { "\"Capture the analyzer's screen.\"", "\"Grab a screenshot of GPIB0::18.\"" });

            Section(sb, "SRQ operation completion",
                "Wait for an operation to ACTUALLY finish via SRQ - driven by the model's data-driven statusModel, not a " +
                "fixed-timeout guess. Also exposes the primitives: serial-poll the status byte (decoded to named bits) and " +
                "wait for SRQ. A missing statusModel can be defined and persisted in one confirm-to-save step.",
                new[] { "\"Wait for the sweep to finish, then read the marker.\"", "\"Serial-poll GPIB0::18.\"" });

            Section(sb, "Error reporting & diagnostics",
                "Tool errors come back as friendly decoded VISA failures; ask for the exact codes to get the raw VISA " +
                "status (name + hex/decimal), the driver text and a timestamp. A per-resource command history shows the " +
                "exact chain of sends/receives leading up to now or to an error.",
                new[] { "\"What was the exact error code?\"", "\"Show the recent commands sent to GPIB0::18.\"" });

            Section(sb, "Configuration",
                "Per-model read/write termination and a default bounded-read length are configurable and persisted. " +
                "Diagnostics: set GPIB_MCP_LOG_LEVEL for log verbosity; GPIB_MCP_BACKEND selects the transport; " +
                "GPIB_MCP_PHANTOM_GPIB_THRESHOLD tunes bus-extender detection.",
                new[] { "\"Set the read terminator for this model to CR/LF.\"" });

            sb.AppendLine("## All tools (" + ToolCount + ")");
            foreach (var tool in _tools.Tools)
                sb.AppendLine("- " + tool.Name + " - " + FirstSentence(tool.Description));

            return sb.ToString().TrimEnd();
        }

        private static void Section(StringBuilder sb, string title, string body, IEnumerable<string> asks)
        {
            sb.AppendLine("## " + title);
            sb.AppendLine(body);
            sb.AppendLine("Try asking: " + string.Join("  ", asks));
            sb.AppendLine();
        }

        /// <summary>First sentence of a tool description, for the compact tool list.</summary>
        private static string FirstSentence(string description)
        {
            if (string.IsNullOrWhiteSpace(description)) return "";
            int dot = description.IndexOf(". ", StringComparison.Ordinal);
            string s = dot >= 0 ? description.Substring(0, dot + 1) : description;
            return s.TrimEnd();
        }
    }
}
