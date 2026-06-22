using System.Collections.Generic;
using Newtonsoft.Json;
using Srq.Completion;

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

        /// <summary>
        /// Optional default bounded read length (bytes) for this model. Set it for a free-running
        /// instrument that streams output continuously and never asserts a normal end-of-response,
        /// so identity/queries read at most this many bytes and return promptly instead of blocking
        /// to the timeout. Null/0 means an ordinary terminator/EOI-bounded read (issue #35).
        /// </summary>
        [JsonProperty("maxReadBytes")] public int? MaxReadBytes { get; set; }

        [JsonProperty("identity")] public IdentitySpec Identity { get; set; }
        [JsonProperty("capture")] public CaptureProfile Capture { get; set; }

        /// <summary>
        /// SRQ/status-byte completion model (the <see cref="Srq.Completion"/> library type). Drives
        /// the data-driven completion waiter; deserialized case-insensitively from the JSON block.
        /// </summary>
        [JsonProperty("statusModel")] public StatusModel StatusModel { get; set; }

        [JsonProperty("commands")] public List<InstrumentCommand> Commands { get; set; }
    }

    /// <summary>How to capture this instrument's screen (e.g. HP-GL plotter emulation).</summary>
    public sealed class CaptureProfile
    {
        /// <summary>Capture method: "hpgl" (HP-GL/PCL plotter emulation) or "scpi_block" (binary image query).</summary>
        [JsonProperty("method")] public string Method { get; set; }

        /// <summary>Command that makes the instrument plot (HP-GL "show"/vector), e.g. "PLOT 550,279,9750,7479;".</summary>
        [JsonProperty("plotCommand")] public string PlotCommand { get; set; }

        /// <summary>
        /// SCPI screen-dump query for <c>method="scpi_block"</c> instruments - returns the screen as an
        /// IEEE 488.2 binary image block (PNG/BMP), e.g. Rigol DS1000Z <c>":DISP:DATA?"</c> or Keysight
        /// <c>":DISP:DATA? PNG,SCReen"</c>. No HP-GL/PCL rendering is needed; the bytes are the image (#10).
        /// </summary>
        [JsonProperty("dumpCommand")] public string DumpCommand { get; set; }

        /// <summary>
        /// Optional command that makes the instrument PRINT (PCL raster hardcopy), e.g. "PRINT;" on the
        /// HP 8560/8590 series. When present, the capture tool can render a PCL "print" as well as an
        /// HP-GL "plot" (issue #40). Null/empty means this model only plots.
        /// </summary>
        [JsonProperty("printCommand")] public string PrintCommand { get; set; }

        /// <summary>Commands to send before plotting/printing, e.g. "SNGLS;TS;" (single sweep, take sweep).</summary>
        [JsonProperty("preRoll")] public string PreRoll { get; set; }

        /// <summary>Optional commands to send after capturing, e.g. "CONTS;" to resume continuous sweep.</summary>
        [JsonProperty("postRoll")] public string PostRoll { get; set; }

        /// <summary>True when this profile can produce a PCL "print" hardcopy in addition to an HP-GL "plot".</summary>
        [JsonIgnore]
        public bool CanPrint => !string.IsNullOrEmpty(PrintCommand);
    }

    /// <summary>Line terminators the instrument expects/returns, if non-default.</summary>
    public sealed class TerminationSpec
    {
        [JsonProperty("write")] public string Write { get; set; }
        [JsonProperty("read")] public string Read { get; set; }

        /// <summary>
        /// The single read-termination character VISA should stop a read on, derived from
        /// <see cref="Read"/> (its last character - e.g. the LF of a "\r\n" sequence). Returns null
        /// when no read terminator is configured, in which case VISA's default termination (EOI) is used.
        /// </summary>
        public char? ReadTerminatorChar() =>
            string.IsNullOrEmpty(Read) ? (char?)null : Read[Read.Length - 1];
    }

    /// <summary>How to ask an instrument what it is, and how to recognise the answer.</summary>
    public sealed class IdentitySpec
    {
        /// <summary>Identification query, e.g. "*IDN?" (SCPI) or "ID?" (legacy HP).</summary>
        [JsonProperty("command")] public string Command { get; set; }

        /// <summary>Regex matched (case-insensitively) against the response to confirm the model.</summary>
        [JsonProperty("matchRegex")] public string MatchRegex { get; set; }

        /// <summary>
        /// Set <c>false</c> for instruments that have NO remote identification query (common on legacy
        /// HP-IB units). The server then reports identity as unavailable - and skips assignment
        /// verification with a clear note - instead of guessing or timing out. Null (or true) means
        /// identification is supported, in which case <see cref="Command"/> must be present.
        /// </summary>
        [JsonProperty("supported")] public bool? Supported { get; set; }

        [JsonProperty("description")] public string Description { get; set; }

        /// <summary>
        /// True when this model can be identified over the bus: not explicitly marked unsupported and
        /// carrying an identification command. False means "do not attempt identity verification".
        /// </summary>
        [JsonIgnore]
        public bool CanIdentify => Supported != false && !string.IsNullOrEmpty(Command);
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

        /// <summary>
        /// The literal value-suffix tokens this parameter accepts on the wire, each paired with the
        /// physical unit it means - so a human value can be resolved to the exact token, converting where
        /// needed (e.g. tokens HZ/KZ/MZ; "1 GHz" -&gt; "1000 MZ"). Read forgivingly: a bare string item is
        /// an unaudited token (<see cref="UnitToken.Unit"/> null) until #46 fills in its meaning.
        /// </summary>
        [JsonProperty("units")]
        [JsonConverter(typeof(UnitTokenListConverter))]
        public List<UnitToken> Units { get; set; }

        [JsonProperty("range")] public string Range { get; set; }
    }

    /// <summary>
    /// A settable parameter's literal wire suffix token and the physical unit it means, e.g.
    /// <c>{ token: "MZ", unit: "MHz" }</c>. The <see cref="Token"/> is what goes on the bus (case as
    /// documented); <see cref="Unit"/> is the standard unit it represents, which lets the resolver convert
    /// a human value to this token (issue #46). <see cref="Unit"/> is null for not-yet-audited tokens.
    /// </summary>
    public sealed class UnitToken
    {
        [JsonProperty("token")] public string Token { get; set; }
        [JsonProperty("unit")] public string Unit { get; set; }

        public UnitToken() { }
        public UnitToken(string token, string unit = null) { Token = token; Unit = unit; }

        /// <summary>True once the token's physical unit is known (so the resolver can use/convert it).</summary>
        [JsonIgnore]
        public bool IsAudited => !string.IsNullOrEmpty(Unit);
    }
}
