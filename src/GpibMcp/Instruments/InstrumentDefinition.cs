using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// A single instrument model's command reference, loaded from a JSON file in the
    /// instrument database. Serialized with Newtonsoft; unknown JSON fields are ignored.
    /// </summary>
    public sealed class InstrumentDefinition
    {
        [JsonProperty("model")] public string Model { get; set; }
        [JsonProperty("manufacturer")] public string Manufacturer { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("source")] public string Source { get; set; }

        /// <summary>Alternate model names this definition also answers to (e.g. "8563EC").</summary>
        [JsonProperty("aliases")]
        [JsonConverter(typeof(FlexibleStringListConverter))]
        public List<string> Aliases { get; set; }

        [JsonProperty("termination")] public TerminationSpec Termination { get; set; }
        [JsonProperty("identity")] public IdentitySpec Identity { get; set; }
        [JsonProperty("capture")] public CaptureProfile Capture { get; set; }
        [JsonProperty("statusModel")] public StatusModel StatusModel { get; set; }
        [JsonProperty("commands")] public List<InstrumentCommand> Commands { get; set; }
    }

    /// <summary>
    /// How an instrument signals operation completion via the GPIB status byte / SRQ. Drives the
    /// data-driven completion waiter so SRQ masks are never hardcoded (issue #12). Optional; absent
    /// means "unknown" and <c>srqSupported:false</c> means the instrument has no usable SRQ.
    /// </summary>
    public sealed class StatusModel
    {
        /// <summary>Whether this instrument supports SRQ-based completion at all.</summary>
        [JsonProperty("srqSupported")] public bool SrqSupported { get; set; } = true;

        [JsonProperty("serialPoll")] public SerialPollSpec SerialPoll { get; set; }
        [JsonProperty("enableMask")] public EnableMaskSpec EnableMask { get; set; }
        [JsonProperty("doneSupport")] public DoneSupportSpec DoneSupport { get; set; }

        /// <summary>Named status-byte bits and their decimal weights (e.g. "endOfSweep" -&gt; 16).</summary>
        [JsonProperty("bits")] public Dictionary<string, int> Bits { get; set; }

        /// <summary>Named operations the waiter can run (e.g. "sweepComplete" -&gt; { arm, expectBit }).</summary>
        [JsonProperty("operations")] public Dictionary<string, StatusOperation> Operations { get; set; }

        /// <summary>The decimal weight of a named bit, or null if unknown.</summary>
        public int? BitValue(string name)
        {
            int value;
            return (name != null && Bits != null && Bits.TryGetValue(name, out value)) ? value : (int?)null;
        }

        /// <summary>Names of the defined bits set in <paramref name="statusByte"/>, highest weight first.</summary>
        public IReadOnlyList<string> SetBitNames(int statusByte)
        {
            var list = new List<string>();
            if (Bits == null) return list;
            foreach (var kv in Bits.OrderByDescending(k => k.Value))
                if (kv.Value != 0 && (statusByte & kv.Value) == kv.Value)
                    list.Add(kv.Key + " (0x" + kv.Value.ToString("X2") + ")");
            return list;
        }
    }

    /// <summary>How a serial poll behaves for this instrument.</summary>
    public sealed class SerialPollSpec
    {
        /// <summary>Whether reading the status byte (serial poll) clears the RQS condition.</summary>
        [JsonProperty("clearsRqs")] public bool ClearsRqs { get; set; }
    }

    /// <summary>Commands that set/clear the SRQ enable mask, with a <c>{mask}</c> placeholder.</summary>
    public sealed class EnableMaskSpec
    {
        /// <summary>Command to enable a mask, e.g. "RQS {mask}" (8560) or "ESTB {mask}" (3325).</summary>
        [JsonProperty("setCommand")] public string SetCommand { get; set; }

        /// <summary>Command to clear the mask, e.g. "RQS 0".</summary>
        [JsonProperty("clearCommand")] public string ClearCommand { get; set; }

        /// <summary>Format of the {mask} substitution: "decimal" (default) or "alpha".</summary>
        [JsonProperty("maskFormat")] public string MaskFormat { get; set; }
    }

    /// <summary>Whether the instrument has an operation-complete ("DONE") mechanism.</summary>
    public sealed class DoneSupportSpec
    {
        [JsonProperty("supported")] public bool Supported { get; set; }
        /// <summary>The mnemonic that requests an operation-complete signal, e.g. "DONE".</summary>
        [JsonProperty("mnemonic")] public string Mnemonic { get; set; }
    }

    /// <summary>A named completion operation: how to arm it, and which status bit confirms it.</summary>
    public sealed class StatusOperation
    {
        /// <summary>Commands that start the operation (the waiter sends the enable mask first), e.g. "TS;".</summary>
        [JsonProperty("arm")] public string Arm { get; set; }

        /// <summary>Name (in <see cref="StatusModel.Bits"/>) of the bit that signals completion.</summary>
        [JsonProperty("expectBit")] public string ExpectBit { get; set; }
    }

    /// <summary>How to capture this instrument's screen (e.g. HP-GL plotter emulation).</summary>
    public sealed class CaptureProfile
    {
        /// <summary>Capture method: "hpgl" (plotter emulation). Future: "scpi_block".</summary>
        [JsonProperty("method")] public string Method { get; set; }

        /// <summary>Command that makes the instrument plot, e.g. "PLOT 550,279,9750,7479;".</summary>
        [JsonProperty("plotCommand")] public string PlotCommand { get; set; }

        /// <summary>Commands to send before plotting, e.g. "SNGLS;TS;" (single sweep, take sweep).</summary>
        [JsonProperty("preRoll")] public string PreRoll { get; set; }

        /// <summary>Optional commands to send after capturing, e.g. "CONTS;" to resume continuous sweep.</summary>
        [JsonProperty("postRoll")] public string PostRoll { get; set; }
    }

    /// <summary>Line terminators the instrument expects/returns, if non-default.</summary>
    public sealed class TerminationSpec
    {
        [JsonProperty("write")] public string Write { get; set; }
        [JsonProperty("read")] public string Read { get; set; }
    }

    /// <summary>How to ask an instrument what it is, and how to recognise the answer.</summary>
    public sealed class IdentitySpec
    {
        /// <summary>Identification query, e.g. "*IDN?" (SCPI) or "ID?" (legacy HP).</summary>
        [JsonProperty("command")] public string Command { get; set; }

        /// <summary>Regex matched (case-insensitively) against the response to confirm the model.</summary>
        [JsonProperty("matchRegex")] public string MatchRegex { get; set; }

        [JsonProperty("description")] public string Description { get; set; }
    }

    /// <summary>One documented command/function the instrument supports.</summary>
    public sealed class InstrumentCommand
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("mnemonic")] public string Mnemonic { get; set; }
        [JsonProperty("category")] public string Category { get; set; }
        [JsonProperty("description")] public string Description { get; set; }

        /// <summary>Set/command form, e.g. "CF &lt;freq&gt;&lt;unit&gt;".</summary>
        [JsonProperty("set")] public string Set { get; set; }

        /// <summary>Query form, e.g. "CF?".</summary>
        [JsonProperty("query")] public string Query { get; set; }

        [JsonProperty("parameters")] public List<CommandParameter> Parameters { get; set; }

        [JsonProperty("examples")]
        [JsonConverter(typeof(FlexibleStringListConverter))]
        public List<string> Examples { get; set; }
    }

    /// <summary>A parameter accepted by a command.</summary>
    public sealed class CommandParameter
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }

        [JsonProperty("units")]
        [JsonConverter(typeof(FlexibleStringListConverter))]
        public List<string> Units { get; set; }

        [JsonProperty("range")] public string Range { get; set; }
    }
}
