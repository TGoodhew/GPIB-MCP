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

        /// <summary>A profile that can both plot (HP-GL) and print (PCL) - the 8560/8590-series shape.</summary>
        private static InstrumentDefinition WithPrintProfile() => new InstrumentDefinition
        {
            Model = "8563E",
            Category = "Spectrum Analyzer",
            Identity = new IdentitySpec { Command = "ID?", MatchRegex = "8563" },
            Capture = new CaptureProfile
            {
                Method = "hpgl",
                PlotCommand = "PLOT 550,279,9750,7479;",
                PrintCommand = "PRINT 0;",
                PreRoll = "SNGLS;TS;"
            },
            Commands = new List<InstrumentCommand>()
        };

        /// <summary>A SCPI-image profile (e.g. a Rigol scope) - captured via a binary block, no HP-GL.</summary>
        private static InstrumentDefinition WithScpiProfile(string dumpCommand = ":DISP:DATA?") => new InstrumentDefinition
        {
            Model = "DS1054Z",
            Category = "Oscilloscope",
            Identity = new IdentitySpec { Command = "*IDN?", MatchRegex = "DS1054Z" },
            Capture = new CaptureProfile { Method = "scpi_block", DumpCommand = dumpCommand },
            Commands = new List<InstrumentCommand>()
        };

        /// <summary>An OUTPPLOT record-loop profile (8720/8753 VNA family) - a plot assembled from records.</summary>
        private static InstrumentDefinition WithOutpplotProfile() => new InstrumentDefinition
        {
            Model = "8720C",
            Category = "Network Analyzer",
            Identity = new IdentitySpec { Command = "OUTPIDEN", MatchRegex = "8720" },
            Capture = new CaptureProfile { Method = "outpplot", DumpCommand = "OUTPPLOT" },
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
            Assert.Contains("INLINE", text);               // names the inline mechanism (issue #48)
            Assert.Contains("do NOT", text);               // explicitly rules out the file route (issue #48)
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
            Assert.Contains("no capture profile", output.AsText());
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

        [Fact]
        public void Capture_PostRoll_IsSentAfterCapture()
        {
            // The pre-roll puts the analyzer in single-sweep (SNGLS;TS;); the post-roll resumes
            // continuous sweep (CONTS;) so the capture doesn't leave the display frozen.
            var def = new InstrumentDefinition
            {
                Model = "8563E",
                Capture = new CaptureProfile
                {
                    Method = "hpgl",
                    PlotCommand = "PLOT 550,279,9750,7479;",
                    PreRoll = "SNGLS;TS;",
                    PostRoll = "CONTS;"
                },
                Commands = new List<InstrumentCommand>()
            };
            var visa = new FakeInstrumentManager();
            var db = InstrumentDatabase.FromDefinitions(new[] { def });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            InstrumentTools.BuildRegistry(visa, db, store).TryGet("instrument_capture_screen", out var tool);

            var output = tool.Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR" });

            Assert.False(output.IsError);
            Assert.Contains("GPIB0::18::INSTR|CONTS;", visa.Writes); // resumed continuous sweep
        }

        [Fact]
        public void Capture_NoFidelityChosen_PromptsToPick_AndUsesHigh()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.False(output.IsError);
            string text = output.AsText();
            Assert.Contains("fidelity", text.ToLowerInvariant());     // prompts the user to choose
            Assert.Contains("use low-fidelity captures", text);       // tells them how to switch
            Assert.DoesNotContain("<text", text);                     // default HIGH -> stroked labels, no <text>
        }

        [Fact]
        public void Capture_LowFidelity_UsesTextLabels_NoPrompt()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["fidelity"] = "low" });

            Assert.False(output.IsError);
            string text = output.AsText();
            Assert.Contains("<text", text);                           // low fidelity -> <text> labels in the SVG
            Assert.Contains("low fidelity", text);                    // noted in the meta line
            Assert.DoesNotContain("hasn't been chosen", text);        // no choose-prompt once a mode is set
        }

        // ---- format = print (PCL, issue #40) --------------------------------

        [Fact]
        public void Capture_PrintFormat_SendsPrintCommand_AndRendersPcl()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithPrintProfile() });
            var visa = new FakeInstrumentManager();
            var output = Tool(db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["format"] = "print" });

            Assert.False(output.IsError);
            // The print (not plot) command drove the capture, in printer-stream mode.
            Assert.Contains("GPIB0::18::INSTR|SNGLS;TS;|PRINT 0;", visa.Captures);
            // A valid PNG was produced from the PCL raster.
            var image = output.Content.Single(b => b.Kind == ToolContentKind.Image);
            var bytes = Convert.FromBase64String(image.Data);
            Assert.True(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50);
            string text = output.AsText();
            Assert.Contains("PCL", text);                              // meta names the format
            Assert.DoesNotContain("fidelity", text.ToLowerInvariant()); // fidelity does not apply to PCL
        }

        [Fact]
        public void Capture_PrintFormat_OnPlotOnlyModel_ReturnsError()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() }); // plot-only
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["format"] = "print" });

            Assert.True(output.IsError);
            Assert.Contains("no PCL print profile", output.AsText());
        }

        [Fact]
        public void Capture_FormatOmitted_PrintCapableModel_PlotsAndPromptsForFormat()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithPrintProfile() });
            var visa = new FakeInstrumentManager();
            var output = Tool(db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.False(output.IsError);
            Assert.Contains("GPIB0::18::INSTR|SNGLS;TS;|PLOT 550,279,9750,7479;", visa.Captures); // defaulted to plot
            string text = output.AsText();
            Assert.Contains("format=\"plot\" or format=\"print\"", text); // nudged to confirm plot-vs-print
            Assert.Contains("SHOW the screen", text);
        }

        [Fact]
        public void Capture_FormatChosen_NoFormatPrompt()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithPrintProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E", ["format"] = "plot" });

            Assert.False(output.IsError);
            Assert.DoesNotContain("format=\"plot\" or format=\"print\"", output.AsText());
        }

        [Fact]
        public void Capture_PlotOnlyModel_NoFormatPrompt()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithCaptureProfile() }); // plot-only
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["model"] = "8563E" });

            Assert.False(output.IsError);
            Assert.DoesNotContain("format=\"plot\" or format=\"print\"", output.AsText());
        }

        // ---- method = scpi_block (binary image dump, issue #10) --------------

        [Fact]
        public void Capture_ScpiBlock_DumpsImage_AndCallsDumpCommand()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithScpiProfile() });
            var visa = new FakeInstrumentManager();
            var output = Tool(db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["resource"] = "USB0::0x1AB1::0x04CE::INSTR", ["model"] = "DS1054Z" });

            Assert.False(output.IsError);
            // The dump command (not a plot/print) drove the capture.
            Assert.Contains("USB0::0x1AB1::0x04CE::INSTR|:DISP:DATA?", visa.BlockQueries);
            // A valid PNG image block was produced from the decoded image.
            var image = output.Content.Single(b => b.Kind == ToolContentKind.Image);
            Assert.Equal("image/png", image.MimeType);
            var bytes = Convert.FromBase64String(image.Data);
            Assert.True(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50);
            Assert.Contains("SCPI dump", output.AsText());
        }

        [Fact]
        public void Capture_ScpiBlock_PointsUserToFullResolutionFile()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithScpiProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "USB0::INSTR", ["model"] = "DS1054Z" });

            Assert.False(output.IsError);
            string text = output.AsText();
            Assert.Contains("FULL-RESOLUTION", text);   // tells the user the inline is a downscaled preview
            Assert.Contains("saved", text);
        }

        [Fact]
        public void Capture_ScpiBlock_MissingDumpCommand_Errors()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithScpiProfile(dumpCommand: null) });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "USB0::INSTR", ["model"] = "DS1054Z" });

            Assert.True(output.IsError);
            Assert.Contains("no dumpCommand", output.AsText());
        }

        // ---- method = outpplot (8720/8753 VNA record loop, issue #55) --------

        [Fact]
        public void Capture_Outpplot_RendersAssembledPlot_AndCallsRecordStream()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithOutpplotProfile() });
            var visa = new FakeInstrumentManager();
            var output = Tool(db, AssignmentStore.InMemory(), visa)
                .Invoke(new JObject { ["resource"] = "GPIB0::16::INSTR", ["model"] = "8720C" });

            Assert.False(output.IsError);
            Assert.Contains("GPIB0::16::INSTR||OUTPPLOT", visa.RecordCaptures);   // looped OUTPPLOT, no preset
            var image = output.Content.Single(b => b.Kind == ToolContentKind.Image);
            var bytes = Convert.FromBase64String(image.Data);
            Assert.True(bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50); // PNG
            Assert.Contains("<svg", output.AsText());                              // a normal vector plot, inline
        }

        [Fact]
        public void Capture_Outpplot_PlotScaleHeader_IsInjectedAfterReset()
        {
            // 8720/8753 OUTPPLOT omits the IP/SC header; the profile supplies it. It must be inserted
            // AFTER the stream's IN/DF reset (DF clears IP/SC), not prepended - here right after "IM;". (#55)
            var def = WithOutpplotProfile();
            def.Capture.PlotScaleHeader = "IP250,279,10250,7479;SC0,4095,0,4212;";
            var db = InstrumentDatabase.FromDefinitions(new[] { def });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::16::INSTR", ["model"] = "8720C", ["return_hpgl"] = true });

            Assert.False(output.IsError);
            // The fake stream begins "IN;SP1;..."; the header lands immediately after the IN; reset.
            Assert.Contains("IN;IP250,279,10250,7479;SC0,4095,0,4212;SP1", output.AsText());
        }

        [Fact]
        public void Capture_Outpplot_NoPlotScaleHeader_LeavesStreamUnchanged()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithOutpplotProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "GPIB0::16::INSTR", ["model"] = "8720C", ["return_hpgl"] = true });

            Assert.False(output.IsError);
            Assert.DoesNotContain("IP250,279,10250,7479;", output.AsText());
        }

        [Fact]
        public void Capture_ScpiBlock_InlineSvgFalse_FallsBackToImageBlock()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { WithScpiProfile() });
            var output = Tool(db, AssignmentStore.InMemory(), new FakeInstrumentManager())
                .Invoke(new JObject { ["resource"] = "USB0::INSTR", ["model"] = "DS1054Z", ["inline_svg"] = false });

            Assert.False(output.IsError);
            Assert.DoesNotContain("<svg", output.AsText());
            Assert.Contains(output.Content, b => b.Kind == ToolContentKind.Image);
        }
    }
}
