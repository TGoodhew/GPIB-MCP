using System;
using System.Collections.Generic;
using System.IO;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Per-instrument read/write termination and the optional bounded read (issue #35):
    /// the model's terminators flow into every query/read, and a free-running instrument can be
    /// bounded by a per-call read_bytes argument or a persisted per-model maxReadBytes.
    /// </summary>
    public class IoTerminationTests
    {
        private static InstrumentDefinition Sample(string model, string read = "\n", string write = "\n",
                                                   int? maxReadBytes = null) => new InstrumentDefinition
        {
            Model = model,
            Manufacturer = "HP",
            Category = "Spectrum Analyzer",
            Termination = new TerminationSpec { Read = read, Write = write },
            MaxReadBytes = maxReadBytes,
            Identity = new IdentitySpec { Command = "*IDN?", MatchRegex = model }
        };

        private static McpTool Tool(string name, InstrumentDatabase db, AssignmentStore store, IInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        // ---- InstrumentIo resolver (unit) ----------------------------------------

        [Fact]
        public void FromDefinition_MapsTerminatorsAndMaxBytes()
        {
            var io = InstrumentIo.FromDefinition(Sample("X", read: "\r\n", write: "\r\n", maxReadBytes: 256), 5000);
            Assert.Equal('\n', io.ReadTermChar);     // last char of "\r\n"
            Assert.Equal("\r\n", io.WriteTerminator);
            Assert.Equal(256, io.MaxReadBytes);
        }

        [Fact]
        public void FromDefinition_NullDefinition_IsPlainTimeoutOnly()
        {
            var io = InstrumentIo.FromDefinition(null, 1234);
            Assert.Equal(1234, io.TimeoutMs);
            Assert.Null(io.ReadTermChar);
            Assert.Null(io.WriteTerminator);
            Assert.Null(io.MaxReadBytes);
        }

        [Fact]
        public void FromDefinition_ReadBytesOverride_WinsOverModelDefault()
        {
            var io = InstrumentIo.FromDefinition(Sample("X", maxReadBytes: 256), 5000, readBytesOverride: 512);
            Assert.Equal(512, io.MaxReadBytes);
        }

        // ---- visa_query / visa_read wiring ---------------------------------------

        [Fact]
        public void VisaQuery_UsesAssignedModelTermination()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E", read: "\r", write: "\r\n") });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            var visa = new FakeInstrumentManager();

            Tool("visa_query", db, store, visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::18::INSTR", ["command"] = "*IDN?" });

            Assert.Equal('\r', visa.LastIo.ReadTermChar);
            Assert.Equal("\r\n", visa.LastIo.WriteTerminator);
            Assert.Null(visa.LastIo.MaxReadBytes);   // bounded read is opt-in, not forced
        }

        [Fact]
        public void VisaQuery_UnassignedResource_HasNoTerminationOrBound()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E") });
            var visa = new FakeInstrumentManager();

            Tool("visa_query", db, AssignmentStore.InMemory(), visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "*IDN?" });

            // No assignment -> historical default behaviour (EOI read, default write terminator).
            Assert.Null(visa.LastIo.ReadTermChar);
            Assert.Null(visa.LastIo.WriteTerminator);
            Assert.Null(visa.LastIo.MaxReadBytes);
        }

        [Fact]
        public void VisaQuery_ReadBytesArg_SetsBoundedRead()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E") });
            var visa = new FakeInstrumentManager();

            Tool("visa_query", db, AssignmentStore.InMemory(), visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "*IDN?", ["read_bytes"] = 512 });

            Assert.Equal(512, visa.LastIo.MaxReadBytes);
        }

        [Fact]
        public void VisaQuery_ModelMaxReadBytes_BoundsReadWithoutArg()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("FREERUN", maxReadBytes: 256) });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::9::INSTR", "FREERUN");
            var visa = new FakeInstrumentManager();

            Tool("visa_query", db, store, visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::9::INSTR", ["command"] = "*IDN?" });

            Assert.Equal(256, visa.LastIo.MaxReadBytes);
        }

        [Fact]
        public void VisaRead_ReadBytesArg_SetsBoundedRead()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E") });
            var visa = new FakeInstrumentManager();

            Tool("visa_read", db, AssignmentStore.InMemory(), visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR", ["read_bytes"] = 100 });

            Assert.Equal(100, visa.LastIo.MaxReadBytes);
        }

        [Fact]
        public void VisaIdentify_ReadBytesArg_SetsBoundedRead()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("8563E") });
            var visa = new FakeInstrumentManager();

            Tool("visa_identify", db, AssignmentStore.InMemory(), visa)
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR", ["read_bytes"] = 512 });

            Assert.Equal(512, visa.LastIo.MaxReadBytes);
        }

        // ---- set_termination -----------------------------------------------------

        [Fact]
        public void SetTermination_ConfirmFlow_PersistsAndBecomesEffective()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gpibterm_" + Path.GetRandomFileName());
            string prevEnv = Environment.GetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB");
            try
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB", tempDir);
                var db = InstrumentDatabase.FromDefinitions(new[] { Sample("FREERUN") });
                var store = AssignmentStore.InMemory();
                var visa = new FakeInstrumentManager();
                var setTerm = Tool("set_termination", db, store, visa);
                string file = Path.Combine(tempDir, "FREERUN.json");

                // Without confirm: proposal only - nothing written, model unchanged.
                var proposed = setTerm.InvokeText(new JObject
                {
                    ["model"] = "FREERUN", ["read_terminator"] = "CRLF", ["max_read_bytes"] = 512
                });
                Assert.Contains("PROPOSED", proposed);
                Assert.False(File.Exists(file));
                db.TryGet("FREERUN", out var before);
                Assert.Null(before.MaxReadBytes);

                // With confirm: file written and the live definition updated.
                var saved = setTerm.InvokeText(new JObject
                {
                    ["model"] = "FREERUN", ["read_terminator"] = "CRLF", ["max_read_bytes"] = 512, ["confirm"] = true
                });
                Assert.Contains("Saved I/O settings", saved);
                Assert.True(File.Exists(file));
                db.TryGet("FREERUN", out var after);
                Assert.Equal(512, after.MaxReadBytes);
                Assert.Equal("\r\n", after.Termination.Read);

                // The persisted file is a minimal override (model + termination + maxReadBytes).
                var json = JObject.Parse(File.ReadAllText(file));
                Assert.Equal("FREERUN", (string)json["model"]);
                Assert.Equal(512, (int)json["maxReadBytes"]);
                Assert.Equal("\r\n", (string)json["termination"]["read"]);

                // And it now flows into a query for that instrument.
                store.Set("GPIB0::9::INSTR", "FREERUN");
                Tool("visa_query", db, store, visa)
                    .InvokeText(new JObject { ["resource"] = "GPIB0::9::INSTR", ["command"] = "*IDN?" });
                Assert.Equal(512, visa.LastIo.MaxReadBytes);
                Assert.Equal('\n', visa.LastIo.ReadTermChar);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB", prevEnv);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SetTermination_ByResource_ResolvesAssignedModel()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gpibterm_" + Path.GetRandomFileName());
            string prevEnv = Environment.GetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB");
            try
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB", tempDir);
                var db = InstrumentDatabase.FromDefinitions(new[] { Sample("FREERUN") });
                var store = AssignmentStore.InMemory();
                store.Set("GPIB0::9::INSTR", "FREERUN");
                var setTerm = Tool("set_termination", db, store, new FakeInstrumentManager());

                var saved = setTerm.InvokeText(new JObject
                {
                    ["resource"] = "GPIB0::9::INSTR", ["max_read_bytes"] = 256, ["confirm"] = true
                });

                Assert.Contains("FREERUN", saved);
                db.TryGet("FREERUN", out var after);
                Assert.Equal(256, after.MaxReadBytes);
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB", prevEnv);
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void SetTermination_NoTarget_AsksForModelOrResource()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("FREERUN") });
            var text = Tool("set_termination", db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .InvokeText(new JObject { ["max_read_bytes"] = 256 });
            Assert.Contains("Provide either", text);
        }

        [Fact]
        public void SetTermination_NoChanges_ReportsNothingToChange()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Sample("FREERUN") });
            var text = Tool("set_termination", db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .InvokeText(new JObject { ["model"] = "FREERUN" });
            Assert.Contains("Nothing to change", text);
        }
    }
}
