using System.Collections.Generic;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// outputSchema + structuredContent (#113): the measurement tools hand back data, not prose the model
    /// has to re-parse. Every adopting tool declares a schema and fills structuredContent on every path it
    /// controls - including its own refusals - so `ok` is the one thing a caller checks.
    /// </summary>
    public class StructuredOutputTests
    {
        /// <summary>A spectrum analyser whose centre-frequency query has audited unit tokens (#46).</summary>
        private static InstrumentDefinition Analyzer() => new InstrumentDefinition
        {
            Model = "8563E",
            Commands = new List<InstrumentCommand>
            {
                new InstrumentCommand
                {
                    Name = "center_frequency", Mnemonic = "CF", Set = "CF <n><unit>", Query = "CF?",
                    Parameters = new List<CommandParameter>
                    {
                        new CommandParameter { Name = "freq", Units = new List<UnitToken>
                            { new UnitToken("HZ", "Hz"), new UnitToken("MZ", "MHz") } }
                    }
                },
                new InstrumentCommand
                {
                    Name = "sweep_time", Mnemonic = "ST", Query = "ST?",
                    Parameters = new List<CommandParameter>
                    {
                        // Not audited: no unit is known, so none may be claimed.
                        new CommandParameter { Name = "t", Units = new List<UnitToken> { new UnitToken("SC") } }
                    }
                }
            }
        };

        private static McpTool Tool(string name, InstrumentDatabase db, AssignmentStore store, IInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        // ---------------------------------------------------------------- tools/list

        [Fact]
        public void ToolsList_CarriesOutputSchema_ForTheStructuredToolsOnly()
        {
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
            var byName = registry.ToListJson().OfType<JObject>().ToDictionary(t => (string)t["name"]);

            foreach (string structured in new[] { "visa_query", "gpib_batch", "resolve_setting", "instrument_reference" })
            {
                Assert.True(byName.ContainsKey(structured), structured + " is missing");
                JToken schema = byName[structured]["outputSchema"];
                Assert.NotNull(schema);
                Assert.Equal("object", (string)schema["type"]);
                Assert.NotNull(schema["properties"]);
            }

            // A tool that only ever returns prose must not claim a schema it cannot honour.
            Assert.Null(byName["visa_write"]["outputSchema"]);
        }

        [Fact]
        public void Dispatcher_EmitsStructuredContent_AlongsideTheText()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            var visa = new FakeInstrumentManager();
            visa.QueryResponses["CF?"] = "1.5E+9";

            var registry = InstrumentTools.BuildRegistry(visa, db, store);
            using (var dispatcher = new McpDispatcher(registry))
            {
                var request = new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                    ["params"] = new JObject
                    {
                        ["name"] = "visa_query",
                        ["arguments"] = new JObject { ["resource"] = "GPIB0::18::INSTR", ["command"] = "CF?" }
                    }
                };

                JObject result = (JObject)dispatcher.Dispatch(request)["result"];
                // Both forms travel together: the text is what a human reads, the structure is what the model uses.
                Assert.Equal("1.5E+9", (string)result["content"][0]["text"]);
                Assert.Equal(1.5e9, (double)result["structuredContent"]["value"]);
                Assert.Equal("Hz", (string)result["structuredContent"]["unit"]);
            }
        }

        // ---------------------------------------------------------------- visa_query

        private static JToken Query(FakeInstrumentManager visa, AssignmentStore store, JObject args)
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            return Tool("visa_query", db, store, visa).Invoke(args).Structured;
        }

        [Fact]
        public void VisaQuery_ReportsTheReadingAsANumberWithItsUnit()
        {
            var visa = new FakeInstrumentManager();
            visa.QueryResponses["CF?"] = "1.5E+9\r\n";
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");

            JToken s = Query(visa, store, new JObject { ["resource"] = "GPIB0::18::INSTR", ["command"] = "CF?" });

            Assert.Equal("GPIB0::18::INSTR", (string)s["resource"]);
            Assert.Equal("CF?", (string)s["command"]);
            Assert.Equal("1.5E+9", (string)s["response"]);   // terminator trimmed, as the text block has it
            Assert.Equal(1.5e9, (double)s["value"]);
            Assert.Equal("Hz", (string)s["unit"]);
            Assert.Equal("8563E", (string)s["model"]);
        }

        [Fact]
        public void VisaQuery_OmitsValue_WhenTheReplyIsNotASingleNumber()
        {
            var visa = new FakeInstrumentManager();
            visa.QueryResponses["*IDN?"] = "HEWLETT-PACKARD,8563E,0,910";

            JToken s = Query(visa, AssignmentStore.InMemory(),
                new JObject { ["resource"] = "GPIB0::18::INSTR", ["command"] = "*IDN?" });

            Assert.Equal("HEWLETT-PACKARD,8563E,0,910", (string)s["response"]);
            Assert.Null(s["value"]);
            Assert.Null(s["unit"]);
            Assert.Null(s["model"]);   // nothing assigned to that resource
        }

        [Fact]
        public void VisaQuery_ClaimsNoUnit_WhenTheCommandsTokensAreNotAudited()
        {
            // An unaudited token means we do not know what the number means - saying nothing beats guessing.
            var visa = new FakeInstrumentManager();
            visa.QueryResponses["ST?"] = "0.05";
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");

            JToken s = Query(visa, store, new JObject { ["resource"] = "GPIB0::18::INSTR", ["command"] = "ST?" });

            Assert.Equal(0.05, (double)s["value"]);
            Assert.Null(s["unit"]);
        }

        // ---------------------------------------------------------------- MeasurementValue

        [Theory]
        [InlineData("1.5E+9", 1.5e9)]
        [InlineData(" -12.34 ", -12.34)]
        [InlineData("+7", 7)]
        public void MeasurementValue_ParsesAPlainNumber(string reply, double expected)
        {
            double value;
            Assert.True(MeasurementValue.TryParseNumber(reply, out value));
            Assert.Equal(expected, value, 6);
        }

        [Theory]
        [InlineData("HEWLETT-PACKARD,8563E")]
        [InlineData("1.0,2.0,3.0")]
        [InlineData("")]
        [InlineData(null)]
        public void MeasurementValue_RefusesAnythingThatIsNotOneNumber(string reply)
        {
            double value;
            Assert.False(MeasurementValue.TryParseNumber(reply, out value));
        }

        [Theory]
        [InlineData("CF?", "Hz")]      // the documented query form
        [InlineData(" cf? ", "Hz")]    // normalized: case and whitespace
        [InlineData("CF", "Hz")]       // the mnemonic, '?' omitted
        [InlineData("ST?", null)]      // known command, unaudited tokens
        [InlineData("XX?", null)]      // not in the database
        public void MeasurementValue_FindsTheUnitOnlyOnAnExactCommandMatch(string command, string expected)
        {
            Assert.Equal(expected, MeasurementValue.UnitForQuery(Analyzer(), command));
        }

        /// <summary>A SCPI instrument as the database documents one: short/long form and optional nodes.</summary>
        private static InstrumentDefinition ScpiGenerator() => new InstrumentDefinition
        {
            Model = "E4438C",
            Commands = new List<InstrumentCommand>
            {
                new InstrumentCommand
                {
                    Name = "frequency_cw", Mnemonic = "[:SOURce]:FREQuency[:CW]", Query = ":FREQuency?",
                    Parameters = new List<CommandParameter>
                    {
                        new CommandParameter { Name = "frequency", Units = new List<UnitToken>
                            { new UnitToken("Hz", "Hz"), new UnitToken("GHz", "GHz") } }
                    }
                },
                new InstrumentCommand
                {
                    Name = "power_amplitude", Mnemonic = "[:SOURce]:POWer[:LEVel]", Query = ":POWer?",
                    Parameters = new List<CommandParameter>
                    {
                        new CommandParameter { Name = "amplitude", Units = new List<UnitToken>
                            { new UnitToken("dBm", "dBm") } }
                    }
                }
            }
        };

        [Theory]
        [InlineData(":FREQ?", "Hz")]            // the short form - what an instrument is actually sent
        [InlineData(":FREQUENCY?", "Hz")]       // the long form
        [InlineData(":freq?", "Hz")]            // case-insensitive
        [InlineData(":SOUR:FREQ:CW?", "Hz")]    // optional nodes spelled out
        [InlineData(":POW?", "dBm")]
        [InlineData(":POWER?", "dBm")]
        [InlineData(":FREQ:STEP?", null)]       // a different command, not this one
        [InlineData(":FRE?", null)]             // shorter than the short form is not an abbreviation
        public void MeasurementValue_UnderstandsScpiShortAndLongForm(string command, string expected)
        {
            // The database documents SCPI in the specification's own notation - ":FREQuency?" - while the
            // wire carries ":FREQ?". Comparing those literally, as the first cut did, meant the unit never
            // resolved for a SCPI instrument. Found on real hardware, not in a test.
            Assert.Equal(expected, MeasurementValue.UnitForQuery(ScpiGenerator(), command));
        }

        // ---------------------------------------------------------------- gpib_batch

        [Fact]
        public void GpibBatch_ReturnsThePreviewEnvelopeAsStructuredContent()
        {
            var visa = new FakeInstrumentManager();
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            ToolOutput output = Tool("gpib_batch", db, AssignmentStore.InMemory(), visa).Invoke(new JObject
            {
                ["preview"] = true,
                ["sweep"] = new JObject { ["var"] = "f", ["from"] = 1, ["to"] = 3, ["step"] = 1 },
                ["steps"] = new JArray(new JObject
                    { ["op"] = "query", ["resource"] = "GPIB0::18::INSTR", ["command"] = "CF?", ["as"] = "cf" })
            });

            JToken s = output.Structured;
            Assert.True((bool)s["ok"]);
            Assert.True((bool)s["preview"]);
            Assert.Equal(3, (int)s["ran"]["points"]);
            // The text block still carries the same envelope, for a client that ignores structured content.
            Assert.Contains("\"points\":3", output.AsText());
        }

        [Fact]
        public void GpibBatch_RejectionStillConformsToTheSchema()
        {
            var visa = new FakeInstrumentManager();
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            ToolOutput output = Tool("gpib_batch", db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["steps"] = new JArray() });

            Assert.True(output.IsError);
            Assert.False((bool)output.Structured["ok"]);
            Assert.False(string.IsNullOrEmpty((string)output.Structured["error"]));
        }

        [Fact]
        public void GpibBatch_RunReturnsRowsAsNumbers()
        {
            var visa = new FakeInstrumentManager();
            visa.QueryResponses["CF?"] = "1.5E+9";
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");

            ToolOutput output = Tool("gpib_batch", db, store, visa).Invoke(new JObject
            {
                ["sweep"] = new JObject { ["var"] = "f", ["from"] = 1, ["to"] = 2, ["step"] = 1 },
                ["steps"] = new JArray(new JObject
                    { ["op"] = "query", ["resource"] = "GPIB0::18::INSTR", ["command"] = "CF?", ["as"] = "cf" })
            });

            JToken s = output.Structured;
            Assert.True((bool)s["ok"]);
            var rows = (JArray)s["rows"];
            Assert.Equal(2, rows.Count);
            // A measured point arrives as a number - the whole point of the exercise.
            Assert.Equal(JTokenType.Float, ((JArray)rows[0])[1].Type);
            Assert.Equal(1.5e9, (double)((JArray)rows[0])[1]);
        }

        // ---------------------------------------------------------------- resolve_setting

        private static InstrumentDefinition Generator() => new InstrumentDefinition
        {
            Model = "8657B",
            Commands = new List<InstrumentCommand>
            {
                new InstrumentCommand { Name = "frequency", Mnemonic = "FR", Set = "FR <value> <unit>",
                    Parameters = new List<CommandParameter> { new CommandParameter { Name = "frequency",
                        Units = new List<UnitToken> { new UnitToken("HZ","Hz"), new UnitToken("MZ","MHz") } } } }
            }
        };

        private static ToolOutput Resolve(JObject args)
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Generator() });
            return Tool("resolve_setting", db, AssignmentStore.InMemory(), new FakeInstrumentManager()).Invoke(args);
        }

        [Fact]
        public void ResolveSetting_ReturnsTheWireStringAsAField()
        {
            ToolOutput output = Resolve(new JObject
                { ["model"] = "8657B", ["command"] = "FR", ["value"] = 1, ["unit"] = "GHz" });

            JToken s = output.Structured;
            Assert.True((bool)s["ok"]);
            Assert.Equal("FR 1000 MZ", (string)s["send"]);
            Assert.Equal("frequency", (string)s["command"]);
            Assert.Equal("MZ", (string)s["resolved"]["token"]);
            Assert.Equal("MHz", (string)s["resolved"]["unit"]);
            Assert.Equal(1000.0, (double)s["resolved"]["value"]);
            Assert.Equal("GHz", (string)s["requested"]["unit"]);
            Assert.Contains("Send: FR 1000 MZ", output.AsText());   // the prose is unchanged
        }

        [Fact]
        public void ResolveSetting_FailureIsStructuredToo()
        {
            JToken s = Resolve(new JObject { ["model"] = "nope", ["command"] = "FR", ["value"] = 1 }).Structured;
            Assert.False((bool)s["ok"]);
            Assert.Contains("Unknown model", (string)s["error"]);
        }

        // ---------------------------------------------------------------- instrument_reference

        [Fact]
        public void InstrumentReference_MarksItsTwoShapesWithOk()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Analyzer() });
            var store = AssignmentStore.InMemory();
            var visa = new FakeInstrumentManager();

            JToken model = Tool("instrument_reference", db, store, visa)
                .Invoke(new JObject { ["model"] = "8563E" }).Structured;
            Assert.True((bool)model["ok"]);
            Assert.Equal("8563E", (string)model["model"]);

            JToken recipe = Tool("instrument_reference", db, store, visa)
                .Invoke(new JObject { ["model"] = "8563E", ["command"] = "CF" }).Structured;
            Assert.True((bool)recipe["ok"]);
            Assert.NotNull(recipe["read"]);

            JToken unknown = Tool("instrument_reference", db, store, visa)
                .Invoke(new JObject { ["model"] = "nope" }).Structured;
            Assert.False((bool)unknown["ok"]);
        }
    }
}
