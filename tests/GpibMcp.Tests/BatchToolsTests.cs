using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class BatchToolsTests
    {
        static BatchToolsTests()
        {
            // gpib_batch writes a per-run breakdown to batch-timing.log; keep it out of the real %LOCALAPPDATA%.
            System.Environment.SetEnvironmentVariable("GPIB_MCP_BATCH_TIMING_LOG",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp-test-batch-timing.log"));
        }

        private static (InstrumentDatabase, AssignmentStore, FakeInstrumentManager) Fixture()
        {
            var def = new InstrumentDefinition { Model = "8563E", Category = "Spectrum Analyzer" };
            var db = InstrumentDatabase.FromDefinitions(new[] { def });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");          // step resources can use the model name "8563E"
            return (db, store, new FakeInstrumentManager());
        }

        private static McpTool Tool(InstrumentDatabase db, AssignmentStore store, FakeInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet("gpib_batch", out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        private static JObject SweepArgs() => new JObject
        {
            ["sweep"] = new JObject { ["var"] = "f_hz", ["from"] = 500000, ["to"] = 1500000, ["step"] = 500000, ["unit"] = "Hz" },
            ["steps"] = new JArray
            {
                new JObject { ["op"] = "write", ["resource"] = "8563E", ["command"] = "CF {{f_hz}}HZ;" },
                new JObject { ["op"] = "query", ["resource"] = "8563E", ["command"] = "MKA?", ["as"] = "amp_dbm" }
            }
        };

        [Fact]
        public void Batch_Sweep_RunsEveryPoint_AndReturnsOneTable()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["MKA?"] = "-12.3";

            var json = JObject.Parse(Tool(db, store, visa).InvokeText(SweepArgs()));

            Assert.True((bool)json["ok"]);
            Assert.Equal(3, (int)json["ran"]["points"]);
            Assert.Equal(6, (int)json["ran"]["gpib_ops"]);                       // 3 points x 2 ops
            Assert.Equal(new[] { "f_hz", "amp_dbm" }, json["columns"].Select(c => (string)c["name"]));
            Assert.Equal("Hz", (string)json["columns"][0]["unit"]);
            var rows = (JArray)json["rows"];
            Assert.Equal(3, rows.Count);
            Assert.Equal(500000, (double)rows[0][0]);
            Assert.Equal(-12.3, (double)rows[0][1]);
            // the swept var really reached the wire, interpolated, at every point:
            Assert.Contains("GPIB0::18::INSTR|CF 500000HZ;", visa.Writes);
            Assert.Contains("GPIB0::18::INSTR|CF 1500000HZ;", visa.Writes);
        }

        [Fact]
        public void Batch_Preview_ReportsPlanSize_WithoutTouchingTheBus()
        {
            var (db, store, visa) = Fixture();
            var args = SweepArgs();
            args["preview"] = true;

            var json = JObject.Parse(Tool(db, store, visa).InvokeText(args));

            Assert.True((bool)json["preview"]);
            Assert.Equal(3, (int)json["ran"]["points"]);
            Assert.Equal(6, (int)json["ran"]["gpib_ops"]);
            Assert.Null(json["needs_confirm"]);                                 // small plan, not gated
            Assert.Empty(visa.Writes);                                          // nothing was sent
        }

        [Fact]
        public void Batch_Result_IncludesSummaryAndMarkdownTable()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["MKA?"] = "-12.3";

            var json = JObject.Parse(Tool(db, store, visa).InvokeText(SweepArgs()));

            string summary = (string)json["summary"];
            Assert.StartsWith("3 points, 6 ops, ", summary);                    // points + ops
            Assert.EndsWith(", 0 errors", summary);                            // error count (elapsed in between)
            string table = (string)json["table"];
            Assert.Contains("| f_hz (Hz) | amp_dbm |", table);                  // header carries the unit
            Assert.Contains("| ---: | ---: |", table);                         // both columns numeric -> right-aligned
            Assert.Contains("| 500000 | -12.3 |", table);                      // a real data row, numbers formatted
            Assert.Equal(3, table.Split('\n').Length - 2);                      // 3 data rows (minus header + divider)
        }

        // A plan above the confirm threshold (~50 GPIB ops) must not touch the bus until confirm:true.
        private static JObject LargeSweepArgs() => new JObject
        {
            ["sweep"] = new JObject { ["var"] = "f_hz", ["from"] = 1000000, ["to"] = 40000000, ["step"] = 1000000 },
            ["steps"] = new JArray
            {
                new JObject { ["op"] = "write", ["resource"] = "8563E", ["command"] = "CF {{f_hz}}HZ;" },
                new JObject { ["op"] = "query", ["resource"] = "8563E", ["command"] = "MKA?", ["as"] = "amp_dbm" }
            }
        };

        [Fact]
        public void Batch_LargePlan_IsGated_AndSendsNothing_UntilConfirmed()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["MKA?"] = "-12.3";

            // 40 points x 2 ops = 80 GPIB ops, over the ConfirmAboveOps=50 threshold.
            var gated = JObject.Parse(Tool(db, store, visa).InvokeText(LargeSweepArgs()));
            Assert.True((bool)gated["needs_confirm"]);
            Assert.True((bool)gated["preview"]);
            Assert.Equal(80, (int)gated["ran"]["gpib_ops"]);
            Assert.Empty(visa.Writes);                                          // gated: nothing sent

            // Re-call with confirm:true -> it actually runs.
            var args = LargeSweepArgs();
            args["confirm"] = true;
            var ran = JObject.Parse(Tool(db, store, visa).InvokeText(args));
            Assert.Null(ran["needs_confirm"]);
            Assert.Equal(40, (int)ran["ran"]["points"]);
            Assert.NotEmpty(visa.Writes);                                       // confirmed: ran on the bus
        }
    }
}
