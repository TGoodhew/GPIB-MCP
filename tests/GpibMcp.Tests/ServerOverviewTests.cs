using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Xunit;

namespace GpibMcp.Tests
{
    public class ServerOverviewTests
    {
        private static InstrumentDefinition Model(string name, string category, params string[] commandNames)
        {
            return new InstrumentDefinition
            {
                Model = name,
                Category = category,
                Commands = commandNames.Select(c => new InstrumentCommand { Name = c }).ToList()
            };
        }

        private static (ToolRegistry, InstrumentDatabase) Build()
        {
            var db = InstrumentDatabase.FromDefinitions(new[]
            {
                Model("8563E", "Spectrum Analyzer", "preset", "center_frequency", "marker_peak"),
                Model("3458A", "Multimeter", "measure", "trigger"),
            });
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager(), db, AssignmentStore.InMemory());
            return (registry, db);
        }

        [Fact]
        public void Detailed_CoversEveryCapabilityArea()
        {
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Detailed();

            Assert.Contains("Discovery", text);
            Assert.Contains("I/O", text);
            Assert.Contains("Identity & assignment", text);
            Assert.Contains("Instrument command database", text);
            Assert.Contains("Screen capture", text);
            Assert.Contains("SRQ operation completion", text);
            Assert.Contains("Error reporting", text);
            Assert.Contains("Configuration", text);
            Assert.Contains("Try asking:", text); // example phrasings present
        }

        [Fact]
        public void Detailed_ListsEveryRegisteredTool_NoDrift()
        {
            // The guard the issue asks for: if a tool is added/removed/renamed, the overview must reflect it.
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Detailed();

            foreach (var tool in registry.Tools)
                Assert.Contains(tool.Name, text);
            Assert.Contains("All tools (" + registry.Count + ")", text);
        }

        [Fact]
        public void Detailed_ReflectsLiveDatabaseCounts()
        {
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Detailed();

            Assert.Contains(db.All.Count.ToString(), text);            // model count (2)
            int commands = db.All.Sum(d => d.Commands.Count);
            Assert.Contains(commands.ToString(), text);                // command count (5)
            Assert.Contains("2 categories", text);                     // distinct categories
        }

        [Fact]
        public void Instructions_IsConcise_AndPointsToTheOverviewTool()
        {
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Instructions();

            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.Contains("gpib_overview", text);                    // steers the model to the detailed tool
            Assert.Contains("GPIB", text);
            Assert.True(text.Length < 1500, "instructions are always-on context and should stay short");
        }

        [Fact]
        public void Instructions_SteerMultiInstrumentLoopsToBatch()
        {
            // #74: a per-point measurement loop spanning several instruments must steer to ONE gpib_batch,
            // not single-op calls per point - in the always-loaded instructions.
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Instructions();

            Assert.Contains("gpib_batch", text);
            Assert.Contains("SEVERAL instruments per point", text);    // the multi-device signal
            Assert.Contains("preamble", text);                         // identify/reset doesn't preclude batching
        }

        [Fact]
        public void Detailed_BatchSection_ShowsAMultiInstrumentExample()
        {
            // #74: the detailed batch section must demonstrate the three-device "configure on B, measure on C"
            // shape and reconcile the SRQ wait with the batch 'complete' step.
            var (registry, db) = Build();
            string text = new ServerOverview(registry, db).Detailed();

            Assert.Contains("SEVERAL instruments at each point", text);
            Assert.Contains("5351A", text);                            // the three-device worked example
            Assert.Contains("'complete' step", text);                  // SRQ wait inside a loop -> batch complete
        }

        [Fact]
        public void OverviewTool_IsRegistered_AndReturnsTheDetailedText()
        {
            var (registry, db) = Build();
            Assert.True(registry.TryGet("gpib_overview", out var tool));

            string toolText = tool.Invoke(new Newtonsoft.Json.Linq.JObject()).AsText();
            Assert.Equal(new ServerOverview(registry, db).Detailed(), toolText);
        }
    }
}
