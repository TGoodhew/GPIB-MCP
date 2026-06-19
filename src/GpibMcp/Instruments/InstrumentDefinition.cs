using System.Collections.Generic;
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
        [JsonProperty("aliases")] public List<string> Aliases { get; set; }

        [JsonProperty("termination")] public TerminationSpec Termination { get; set; }
        [JsonProperty("identity")] public IdentitySpec Identity { get; set; }
        [JsonProperty("commands")] public List<InstrumentCommand> Commands { get; set; }
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
        [JsonProperty("examples")] public List<string> Examples { get; set; }
    }

    /// <summary>A parameter accepted by a command.</summary>
    public sealed class CommandParameter
    {
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("units")] public List<string> Units { get; set; }
        [JsonProperty("range")] public string Range { get; set; }
    }
}
