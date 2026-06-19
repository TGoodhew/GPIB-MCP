using System.Collections.Generic;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class DatabaseToolsTests
    {
        private static InstrumentDefinition Sample(string model) => new InstrumentDefinition
        {
            Model = model,
            Manufacturer = "HP",
            Category = "Spectrum Analyzer",
            Identity = new IdentitySpec { Command = "ID?", MatchRegex = model },
            Commands = new List<InstrumentCommand>
            {
                new InstrumentCommand { Name = "center_frequency", Mnemonic = "CF", Category = "Frequency",
                    Description = "Sets the center frequency.", Set = "CF <n><unit>", Query = "CF?" }
            }
        };

        private static McpTool Tool(string name, InstrumentDatabase db, AssignmentStore store, IInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        private static (InstrumentDatabase, AssignmentStore, FakeInstrumentManager) Fixture()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E") });
            return (db, AssignmentStore.InMemory(), new FakeInstrumentManager());
        }

        [Fact]
        public void ListModels_ReportsKnownInstruments()
        {
            var (db, store, visa) = Fixture();
            var text = Tool("instrument_list_models", db, store, visa).Invoke(new JObject());
            Assert.Contains("8563E", text);
            Assert.Contains("Spectrum Analyzer", text);
        }

        [Fact]
        public void Reference_UnknownModel_ReportsUnknown()
        {
            var (db, store, visa) = Fixture();
            var text = Tool("instrument_reference", db, store, visa).Invoke(new JObject { ["model"] = "ZZZ" });
            Assert.Contains("Unknown model", text);
        }

        [Fact]
        public void Reference_KnownModel_ReturnsIndex()
        {
            var (db, store, visa) = Fixture();
            var text = Tool("instrument_reference", db, store, visa).Invoke(new JObject { ["model"] = "8563E" });
            var json = JObject.Parse(text);
            Assert.Equal("8563E", (string)json["model"]);
            Assert.NotNull(json["commandIndex"]);
        }

        [Fact]
        public void Reference_SpecificCommand_ReturnsDetail()
        {
            var (db, store, visa) = Fixture();
            var text = Tool("instrument_reference", db, store, visa)
                .Invoke(new JObject { ["model"] = "8563E", ["command"] = "CF" });
            var json = JObject.Parse(text);
            Assert.Equal("center_frequency", (string)json["name"]);
            Assert.Equal("CF?", (string)json["query"]);
        }

        [Fact]
        public void Assign_WithoutConfirm_DoesNotPersist()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["ID?"] = "HP8563E";

            var text = Tool("assign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.Contains("NOT yet saved", text);
            Assert.Contains("matches 8563E", text);   // verification ran
            Assert.Empty(store.All());                 // but nothing persisted
        }

        [Fact]
        public void Assign_WithConfirm_Persists()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["ID?"] = "HP8563E";

            var text = Tool("assign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["confirm"] = true });

            Assert.Contains("Saved assignment", text);
            Assert.Equal("8563E", store.Get("GPIB0::18::INSTR"));
        }

        [Fact]
        public void Assign_UnknownModel_ReportsUnknown()
        {
            var (db, store, visa) = Fixture();
            var text = Tool("assign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "NOPE", ["confirm"] = true });
            Assert.Contains("Unknown model", text);
            Assert.Empty(store.All());
        }

        [Fact]
        public void Assign_IdentityMismatch_IsFlagged()
        {
            var (db, store, visa) = Fixture();
            visa.QueryResponses["ID?"] = "SOMETHING ELSE";

            var text = Tool("assign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.Contains("does NOT match", text);
        }

        [Fact]
        public void ListAssignments_ReflectsStore()
        {
            var (db, store, visa) = Fixture();
            store.Set("GPIB0::18::INSTR", "8563E");
            var text = Tool("list_assignments", db, store, visa).Invoke(new JObject());
            Assert.Contains("GPIB0::18::INSTR -> 8563E", text);
        }

        [Fact]
        public void Unassign_ConfirmFlow()
        {
            var (db, store, visa) = Fixture();
            store.Set("GPIB0::18::INSTR", "8563E");

            var proposal = Tool("unassign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR" });
            Assert.Contains("Call again with confirm=true", proposal);
            Assert.Equal("8563E", store.Get("GPIB0::18::INSTR")); // still there

            var done = Tool("unassign_instrument", db, store, visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["confirm"] = true });
            Assert.Contains("Removed", done);
            Assert.Null(store.Get("GPIB0::18::INSTR"));
        }
    }
}
