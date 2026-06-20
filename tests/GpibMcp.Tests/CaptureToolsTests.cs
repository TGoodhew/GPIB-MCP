using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class CaptureToolsTests
    {
        static CaptureToolsTests()
        {
            // Keep default-path captures out of %LOCALAPPDATA% during tests.
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR",
                Path.Combine(Path.GetTempPath(), "gpibmcp_captures_test"));
        }

        private static InstrumentDefinition WithCaptureProfile() => new InstrumentDefinition
        {
            Model = "8563E",
            Category = "Spectrum Analyzer",
            Identity = new IdentitySpec { Command = "ID?", MatchRegex = "8563" },
            Capture = new CaptureProfile
            {
                Method = "hpgl",
                PlotCommand = "PLOT 550,279,9750,7479;",
                PreRoll = "SNGLS;TS;"
            },
            Commands = new List<InstrumentCommand>()
        };

        private static McpTool Tool(InstrumentDatabase db, AssignmentStore store, IInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet("instrument_capture_screen", out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        [Fact]
        public void Capture_AssignedModel_ReturnsImageBlock()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            var visa = new FakeInstrumentManager();

            var output = Tool(db, store, visa).Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR" });

            Assert.False(output.IsError);
            var image = output.Content.Single(b => b.Kind == ToolContentKind.Image);
            Assert.Equal("image/png", image.MimeType);
            // base64 decodes to a valid PNG
            var bytes = Convert.FromBase64String(image.Data);
            Assert.True(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50);
            // the profile drove the capture
            Assert.Contains("GPIB0::18::INSTR|SNGLS;TS;|PLOT 550,279,9750,7479;", visa.Captures);
        }

        [Fact]
        public void Capture_ModelOverride_UsedWhenUnassigned()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });
            Assert.False(output.IsError);
            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Image);
        }

        [Fact]
        public void Capture_ReturnHpgl_AddsTextBlock()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["return_hpgl"] = true });

            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Image);
            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Text && b.Text.Contains("PLOT") == false && b.Text.Contains("SP0;"));
        }

        [Fact]
        public void Capture_SavesPngToFile_AndReportsPath()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            string path = Path.Combine(Path.GetTempPath(), "captest_" + Path.GetRandomFileName() + ".png");
            try
            {
                var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                    .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["save_path"] = path });

                Assert.False(output.IsError);
                Assert.True(File.Exists(path));
                Assert.Contains("saved to: " + path, output.AsText());
                var bytes = File.ReadAllBytes(path);
                Assert.True(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50); // PNG
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Capture_Default_ReturnsInlineSvgForArtifact()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.False(output.IsError);
            string text = output.AsText();
            Assert.Contains("<svg", text);                 // SVG handed to the model to render inline
            Assert.Contains("artifact", text);             // instruction to display it
            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Image); // raster kept too
        }

        [Fact]
        public void Capture_InlineSvgFalse_FallsBackToImageOnly()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["inline_svg"] = false });

            Assert.False(output.IsError);
            Assert.DoesNotContain("<svg", output.AsText());
            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Image);
        }

        [Fact]
        public void Capture_SaveDir_WritesIntoFolderAndReportsPath()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            string dir = Path.Combine(Path.GetTempPath(), "captest_" + Path.GetRandomFileName());
            try
            {
                var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                    .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["save_dir"] = dir });

                Assert.False(output.IsError);
                var pngs = Directory.GetFiles(dir, "*.png");
                Assert.Single(pngs);
                Assert.Contains("saved to: " + pngs[0], output.AsText());
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        }

        [Fact]
        public void Capture_NoModelKnown_ReturnsError()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR" }); // unassigned, no model arg
            Assert.True(output.IsError);
            Assert.Contains("No model is known", output.AsText());
        }

        [Fact]
        public void Capture_ModelWithoutProfile_ReturnsError()
        {
            var def = new InstrumentDefinition { Model = "3325A", Category = "Source" }; // no capture profile
            var db = InstrumentDatabase.FromDefinitions(new[] { def });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::7::INSTR", ["model"] = "3325A" });
            Assert.True(output.IsError);
            Assert.Contains("no HP-GL capture profile", output.AsText());
        }

        [Fact]
        public void Capture_SubThresholdPlot_ReturnsError()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var visa = new FakeInstrumentManager { CaptureHpgl = "SP1;PU;SP0;" }; // < 128 bytes
            var output = Tool(db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });
            Assert.True(output.IsError);
            Assert.Contains("No complete plot", output.AsText());
        }
    }
}
