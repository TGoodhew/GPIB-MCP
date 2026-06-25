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
    [Collection("CaptureFiles")]
    public class RawBytePassthroughTests
    {
        static RawBytePassthroughTests()
        {
            // The round-trip test runs a real capture (renders + saves a PNG, appends capture-timing.log,
            // retains the forwardable bytes #79); keep them all out of the real %LOCALAPPDATA%/Pictures.
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_DIR",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_captures_test"));
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURE_TIMING_LOG",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_captures_test", "capture-timing.log"));
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURES_DIR",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_captures_test", "handles"));
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
        public void VisaWriteRaw_StreamsPaced_PassingChunkBytesThrough_Losslessly()
        {
            var visa = new FakeInstrumentManager();
            byte[] payload = { 0x1B, 0x03, 0x00, 0x0E, 0x0F, (byte)'A', 0x7E };

            var output = Tool(visa, "visa_write_raw").Invoke(new JObject
            {
                ["resource"] = "GPIB0::6::INSTR",
                ["data"] = Convert.ToBase64String(payload),
                ["chunk_bytes"] = 128
            });

            Assert.False(output.IsError);
            Assert.Equal(128, visa.LastStreamChunkBytes);           // the paced chunk size reached the manager
            Assert.Equal(payload, Assert.Single(visa.RawWrites).Data); // still byte-for-byte
        }

        [Fact]
        public void VisaWriteRaw_Debug_SavesTheExactBytesToDisk()
        {
            var visa = new FakeInstrumentManager();
            byte[] payload = { (byte)'P', (byte)'D', 0x03, 0x00, 0x0E };
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gpibmcp_dbg_" + System.IO.Path.GetRandomFileName());
            string prev = Environment.GetEnvironmentVariable("GPIB_MCP_DEBUG_DIR");
            Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", dir);
            try
            {
                Tool(visa, "visa_write_raw").Invoke(new JObject
                {
                    ["resource"] = "GPIB0::6::INSTR",
                    ["data"] = Convert.ToBase64String(payload),
                    ["debug"] = true
                });

                var dumped = System.IO.Directory.GetFiles(dir, "*.hpgl");
                Assert.Single(dumped);
                Assert.Equal(payload, System.IO.File.ReadAllBytes(dumped[0]));   // exact bytes on disk
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", prev);
                try { System.IO.Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
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

        // ---- #79: send by reference (handle), not base64 through the model ----

        [Fact]
        public void VisaWriteRaw_FromPath_StreamsTheExactFileBytes()
        {
            var visa = new FakeInstrumentManager();
            byte[] payload = { 0x1B, 0x03, 0x00, 0x0E, 0x0F, (byte)'A', 0x7E };
            string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "wr_" + System.IO.Path.GetRandomFileName() + ".hpgl");
            System.IO.File.WriteAllBytes(file, payload);
            try
            {
                var output = Tool(visa, "visa_write_raw").Invoke(new JObject
                {
                    ["resource"] = "GPIB0::6::INSTR",
                    ["path"] = file              // by reference - no base64 in the args
                });

                Assert.False(output.IsError);
                Assert.Equal(payload, Assert.Single(visa.RawWrites).Data);   // exact file bytes reached the plotter
            }
            finally { try { System.IO.File.Delete(file); } catch { } }
        }

        [Fact]
        public void VisaWriteRaw_RequiresExactlyOneOfDataOrPath()
        {
            var visa = new FakeInstrumentManager();
            // Neither supplied.
            Assert.Throws<ArgumentException>(() => Tool(visa, "visa_write_raw")
                .Invoke(new JObject { ["resource"] = "GPIB0::6::INSTR" }));
            // Both supplied.
            Assert.Throws<ArgumentException>(() => Tool(visa, "visa_write_raw").Invoke(new JObject
            {
                ["resource"] = "GPIB0::6::INSTR",
                ["data"] = Convert.ToBase64String(new byte[] { 1, 2, 3 }),
                ["path"] = "C:\\nope.hpgl"
            }));
            Assert.Empty(visa.RawWrites);
        }

        [Fact]
        public void VisaWriteRaw_Path_DoesNotExist_Throws()
        {
            var visa = new FakeInstrumentManager();
            var args = new JObject
            {
                ["resource"] = "GPIB0::6::INSTR",
                ["path"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "no_such_" + System.IO.Path.GetRandomFileName() + ".hpgl")
            };
            Assert.Throws<ArgumentException>(() => Tool(visa, "visa_write_raw").Invoke(args));
            Assert.Empty(visa.RawWrites);
        }

        [Fact]
        public void RoundTrip_CaptureHandle_ToVisaWriteRawByReference_PreservesEveryByte()
        {
            // The #79 headline: forwarding is driven by the capture HANDLE (a short path), never the bytes -
            // yet the plotter still receives the exact captured plot, ETX intact.
            var (db, store, visa) = CaptureFixture();
            var registry = InstrumentTools.BuildRegistry(visa, db, store);

            registry.TryGet("instrument_capture_screen", out var capture);
            var captured = capture.Invoke(new JObject { ["resource"] = "GPIB0::18::INSTR" });   // no return_hpgl_base64
            string handle = ExtractHandle(captured);
            Assert.True(System.IO.File.Exists(handle));

            registry.TryGet("visa_write_raw", out var writeRaw);
            var sent = writeRaw.Invoke(new JObject { ["resource"] = "GPIB0::6::INSTR", ["path"] = handle });

            Assert.False(sent.IsError);
            var (plotter, written) = Assert.Single(visa.RawWrites);
            Assert.Equal("GPIB0::6::INSTR", plotter);
            Assert.Equal(Latin1.GetBytes(visa.CaptureHpgl), written);   // exact captured plot, by reference
            Assert.Contains((byte)0x03, written);                       // ETX label terminator survived
        }

        // ---- helpers ----

        /// <summary>Pulls the send-by-reference handle path out of a capture result's forwarding hint.</summary>
        private static string ExtractHandle(ToolOutput output)
        {
            foreach (var b in output.Content)
            {
                if (b.Kind != ToolContentKind.Text || b.Text == null) continue;
                int i = b.Text.IndexOf("path=\"", StringComparison.Ordinal);
                if (i < 0) continue;
                i += "path=\"".Length;
                int j = b.Text.IndexOf('"', i);
                if (j > i) return b.Text.Substring(i, j - i);
            }
            throw new Xunit.Sdk.XunitException("no send-by-reference handle in capture output");
        }

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
