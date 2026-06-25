using System;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #70: raw-byte plot pass-through. The capture→resend round-trip must preserve every byte - including
    /// non-printing control bytes (HP-GL ETX 0x03, SO/SI 0x0E/0x0F, NUL) - by carrying them as base64 across
    /// the tool boundary and writing them verbatim, never through a string/terminator path.
    /// </summary>
    public class RawBytePassthroughTests
    {
        static RawBytePassthroughTests()
        {
            // The round-trip test runs a real capture (renders + saves a PNG, appends capture-timing.log);
            // keep both out of the real %LOCALAPPDATA%/Pictures during tests.
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_captures_test"));
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_TIMING_LOG",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_captures_test", "capture-timing.log"));
        }

        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private static McpTool Tool(IInstrumentManager visa, string name)
        {
            InstrumentTools.BuildRegistry(visa).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        [Fact]
        public void VisaWriteRaw_WritesTheExactDecodedBytes_IncludingControlBytes()
        {
            var visa = new FakeInstrumentManager();
            byte[] payload = { 0x1B, 0x03, 0x00, 0x0E, 0x0F, (byte)'A', 0x7E };   // ESC, ETX, NUL, SO, SI, 'A', '~'

            var output = Tool(visa, "visa_write_raw").Invoke(new JObject
            {
                ["resource"] = "GPIB0::6::INSTR",
                ["data"] = Convert.ToBase64String(payload)
            });

            Assert.False(output.IsError);
            var (resource, written) = Assert.Single(visa.RawWrites);
            Assert.Equal("GPIB0::6::INSTR", resource);
            Assert.Equal(payload, written);                         // byte-for-byte, control bytes intact
        }

        [Fact]
        public void VisaWriteRaw_RejectsInvalidBase64()
        {
            var visa = new FakeInstrumentManager();
            var args = new JObject { ["resource"] = "GPIB0::6::INSTR", ["data"] = "not base64 !!!" };

            Assert.Throws<ArgumentException>(() => Tool(visa, "visa_write_raw").Invoke(args));
            Assert.Empty(visa.RawWrites);                           // nothing written on a bad payload
        }

        [Fact]
        public void Capture_ReturnHpglBase64_DecodesToTheExactPlotBytes_WithEtxIntact()
        {
            var (db, store, visa) = CaptureFixture();

            var output = Tool2(db, store, visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::18::INSTR",
                ["return_hpgl_base64"] = true
            });

            byte[] decoded = Convert.FromBase64String(ExtractBase64(output));
            Assert.Equal(Latin1.GetBytes(visa.CaptureHpgl), decoded);   // exact captured bytes
            Assert.Contains((byte)0x03, decoded);                       // the LB ETX terminator survived
        }

        [Fact]
        public void RoundTrip_CaptureBase64_ToVisaWriteRaw_PreservesEveryByte()
        {
            // The headline acceptance criterion: capture base64 == bytes written to the plotter.
            var (db, store, visa) = CaptureFixture();
            var registry = InstrumentTools.BuildRegistry(visa, db, store);

            registry.TryGet("instrument_capture_screen", out var capture);
            var captured = capture.Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR", ["return_hpgl_base64"] = true });
            string b64 = ExtractBase64(captured);

            registry.TryGet("visa_write_raw", out var writeRaw);
            var sent = writeRaw.Invoke(new JObject { ["resource"] = "GPIB0::6::INSTR", ["data"] = b64 });

            Assert.False(sent.IsError);
            var (plotter, written) = Assert.Single(visa.RawWrites);
            Assert.Equal("GPIB0::6::INSTR", plotter);
            Assert.Equal(Latin1.GetBytes(visa.CaptureHpgl), written);   // plotter gets the exact captured plot
            Assert.Contains((byte)0x03, written);                       // including the ETX label terminator
        }

        // ---- helpers ----

        private static (InstrumentDatabase, AssignmentStore, FakeInstrumentManager) CaptureFixture()
        {
            var def = new InstrumentDefinition
            {
                Model = "8563E",
                Category = "Spectrum Analyzer",
                Capture = new CaptureProfile { Method = "hpgl", PlotCommand = "PLOT 0,0,9000,7000;" }
            };
            var db = InstrumentDatabase.FromDefinitions(new[] { def });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            return (db, store, new FakeInstrumentManager());
        }

        private static McpTool Tool2(InstrumentDatabase db, AssignmentStore store, IInstrumentManager visa)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet("instrument_capture_screen", out var tool);
            return tool;
        }

        /// <summary>Pulls the base64 blob out of the return_hpgl_base64 text block (it's the last line).</summary>
        private static string ExtractBase64(ToolOutput output)
        {
            string block = output.Content
                .Where(b => b.Kind == ToolContentKind.Text && b.Text.Contains("base64"))
                .Select(b => b.Text)
                .Single(t => t.StartsWith("RAW "));
            return block.Substring(block.LastIndexOf('\n') + 1).Trim();
        }
    }
}
