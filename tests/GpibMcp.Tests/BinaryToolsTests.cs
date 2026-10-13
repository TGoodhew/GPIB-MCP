using System;
using System.IO;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>#139: visa_query_binary, binary replies to visa_query, and the E4406A's GIF screen dump.</summary>
    public class BinaryToolsTests : IDisposable
    {
        /// <summary>A real 24x16 GIF89a (877 bytes), as an E4406A returns its screen - only smaller.</summary>
        private static readonly byte[] Gif = Convert.FromBase64String(
            "R0lGODlhGAAQAPcAAAAAAAAAMwAAZgAAmQAAzAAA/wArAAArMwArZgArmQArzAAr/wBVAABVMwBVZgBVmQBVzABV/wCAAACAMwCAZgCAmQCAzACA/wCqAACqMwCqZgCqmQCqzACq/wDVAADVMwDVZgDVmQDVzADV/wD/AAD/MwD/ZgD/mQD/zAD//zMAADMAMzMAZjMAmTMAzDMA/zMrADMrMzMrZjMrmTMrzDMr/zNVADNVMzNVZjNVmTNVzDNV/zOAADOAMzOAZjOAmTOAzDOA/zOqADOqMzOqZjOqmTOqzDOq/zPVADPVMzPVZjPVmTPVzDPV/zP/ADP/MzP/ZjP/mTP/zDP//2YAAGYAM2YAZmYAmWYAzGYA/2YrAGYrM2YrZmYrmWYrzGYr/2ZVAGZVM2ZVZmZVmWZVzGZV/2aAAGaAM2aAZmaAmWaAzGaA/2aqAGaqM2aqZmaqmWaqzGaq/2bVAGbVM2bVZmbVmWbVzGbV/2b/AGb/M2b/Zmb/mWb/zGb//5kAAJkAM5kAZpkAmZkAzJkA/5krAJkrM5krZpkrmZkrzJkr/5lVAJlVM5lVZplVmZlVzJlV/5mAAJmAM5mAZpmAmZmAzJmA/5mqAJmqM5mqZpmqmZmqzJmq/5nVAJnVM5nVZpnVmZnVzJnV/5n/AJn/M5n/Zpn/mZn/zJn//8wAAMwAM8wAZswAmcwAzMwA/8wrAMwrM8wrZswrmcwrzMwr/8xVAMxVM8xVZsxVmcxVzMxV/8yAAMyAM8yAZsyAmcyAzMyA/8yqAMyqM8yqZsyqmcyqzMyq/8zVAMzVM8zVZszVmczVzMzV/8z/AMz/M8z/Zsz/mcz/zMz///8AAP8AM/8AZv8Amf8AzP8A//8rAP8rM/8rZv8rmf8rzP8r//9VAP9VM/9VZv9Vmf9VzP9V//+AAP+AM/+AZv+Amf+AzP+A//+qAP+qM/+qZv+qmf+qzP+q///VAP/VM//VZv/Vmf/VzP/V////AP//M///Zv//mf//zP///wAAAAAAAAAAAAAAACH5BAEAAPwALAAAAAAYABAAAAhKAAEIHEiwoEGB9g4qNGgv4cKHABxCVNhQoYSLGA9KPIgxI8GKDzte/DgRgEgJCDeGFAmypEmRLjWqjBmRZsGWNmvmTLkTYc+BAQEAOw==");

        private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpibmcp-binary-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        private static byte[] Block(byte[] payload)
        {
            string length = payload.Length.ToString("000");
            return Encoding.ASCII.GetBytes("#" + length.Length + length).Concat(payload).Concat(new[] { (byte)'\n' }).ToArray();
        }

        /// <summary>A 24-bit BMP of random noise: it doesn't compress, so its PNG is large (800x600 is ~1.4 MB).</summary>
        private static byte[] NoiseBmp(int w, int h)
        {
            int row = (w * 3 + 3) & ~3, size = 54 + row * h;
            var b = new byte[size];
            b[0] = (byte)'B'; b[1] = (byte)'M';
            BitConverter.GetBytes(size).CopyTo(b, 2);
            BitConverter.GetBytes(54).CopyTo(b, 10);
            BitConverter.GetBytes(40).CopyTo(b, 14);
            BitConverter.GetBytes(w).CopyTo(b, 18);
            BitConverter.GetBytes(h).CopyTo(b, 22);
            BitConverter.GetBytes((short)1).CopyTo(b, 26);
            BitConverter.GetBytes((short)24).CopyTo(b, 28);
            var rng = new Random(1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w * 3; x++)
                    b[54 + y * row + x] = (byte)rng.Next(256);
            return b;
        }

        private static McpTool Tool(string name, IInstrumentManager visa, InstrumentDatabase db = null, AssignmentStore store = null)
        {
            InstrumentTools.BuildRegistry(visa, db ?? InstrumentDatabase.Empty(), store ?? AssignmentStore.InMemory()).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        // ---- visa_query_binary ---------------------------------------------------

        [Fact]
        public void QueryBinary_SavesTheBytes_DescribesThem_AndReturnsThePngImage()
        {
            var visa = new FakeInstrumentManager { BinaryResponse = Block(Gif) };

            ToolOutput output = Tool("visa_query_binary", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = ":HCOP:SDUM:DATA? GIF", ["save_dir"] = _dir
            });

            Assert.False(output.IsError);
            Assert.Equal("GPIB0::17::INSTR|:HCOP:SDUM:DATA? GIF|Ieee4882", Assert.Single(visa.BinaryQueries));
            var s = (JObject)output.Structured;
            Assert.Equal(Gif.Length, (int)s["bytes"]);
            Assert.Equal("GIF", (string)s["type"]);
            Assert.Equal("#3877", (string)s["header"]);
            Assert.StartsWith("47 49 46 38 39 61", (string)s["first_bytes_hex"]);                      // GIF89a
            string saved = (string)s["saved_to"];
            Assert.EndsWith(".gif", saved);
            Assert.Equal(Gif, File.ReadAllBytes(saved));                                                // the original, byte for byte
            string sha;
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                sha = string.Concat(sha256.ComputeHash(Gif).Select(x => x.ToString("x2")));
            Assert.Equal(sha, (string)s["sha256"]);
            Assert.EndsWith(".png", (string)s["png_saved_to"]);                                         // PNG beside it
            Assert.True(File.Exists((string)s["png_saved_to"]));
            Assert.Contains(output.Content, c => c.Kind == ToolContentKind.Image && c.MimeType == "image/png");      // shown as PNG, not GIF
        }

        [Fact]
        public void QueryBinary_IsLongRunning()
        {
            // A slow GPIB transfer can take 30 s: task-capable clients get a handle to poll, not a blocked call.
            Assert.True(Tool("visa_query_binary", new FakeInstrumentManager()).LongRunning);
        }

        [Fact]
        public void QueryBinary_APngReply_IsSavedAsSent_NotOverwrittenByItsConversion()
        {
            // A PNG with trailing bytes after IEND: re-encoding it gives a different file of a different length.
            byte[] png = Hpgl.Rendering.ScreenImage.ToPng(Gif).Concat(new byte[100]).ToArray();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(png) };

            var s = (JObject)Tool("visa_query_binary", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = ":DISP:DATA? PNG", ["save_dir"] = _dir
            }).Structured;

            Assert.Equal("PNG", (string)s["type"]);
            Assert.Equal(png, File.ReadAllBytes((string)s["saved_to"]));
            Assert.Equal((string)s["saved_to"], (string)s["png_saved_to"]);
            Assert.Single(Directory.GetFiles(_dir));
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                Assert.Equal(string.Concat(sha256.ComputeHash(File.ReadAllBytes((string)s["saved_to"])).Select(x => x.ToString("x2"))),
                             (string)s["sha256"]);
        }

        [Fact]
        public void QueryBinary_ALargeImage_IsSavedFullSize_AndOnlyTheAttachedCopyIsScaledDown()
        {
            var visa = new FakeInstrumentManager { BinaryResponse = Block(NoiseBmp(800, 600)) };

            ToolOutput output = Tool("visa_query_binary", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = ":DISP:DATA? BMP", ["save_dir"] = _dir
            });

            byte[] savedPng = File.ReadAllBytes((string)((JObject)output.Structured)["png_saved_to"]);
            Hpgl.Rendering.ScreenImage.Dimensions(savedPng, out int w, out _);
            Assert.Equal(800, w);
            Assert.True(savedPng.Length > BinaryTools.MaxImageContentBytes);
            AssertAttachedImageIsScaledDown(output);
        }

        private static void AssertAttachedImageIsScaledDown(ToolOutput output)
        {
            byte[] attached = Convert.FromBase64String(output.Content.Single(c => c.Kind == ToolContentKind.Image).Data);
            Assert.True(attached.Length <= BinaryTools.MaxImageContentBytes, "attached PNG is " + attached.Length + " bytes");
            Hpgl.Rendering.ScreenImage.Dimensions(attached, out int w, out _);
            Assert.True(w < 800);
        }

        [Fact]
        public void QueryBinary_RawMode_ReadsUnframedBytes()
        {
            var visa = new FakeInstrumentManager { BinaryResponse = Gif };

            var s = Tool("visa_query_binary", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = "DUMP?", ["block_format"] = "raw", ["save_dir"] = _dir
            }).Structured as JObject;

            Assert.Equal(Gif.Length, (int)s["bytes"]);
            Assert.Null(s["header"]);
        }

        [Fact]
        public void QueryBinary_UnknownBytes_AreSavedAsBin_WithNoImage()
        {
            byte[] trace = Enumerable.Range(0, 300).Select(i => (byte)i).ToArray();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(trace) };

            ToolOutput output = Tool("visa_query_binary", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = ":TRAC:DATA?", ["save_dir"] = _dir
            });

            Assert.Equal("unknown", (string)((JObject)output.Structured)["type"]);
            Assert.EndsWith(".bin", (string)((JObject)output.Structured)["saved_to"]);
            Assert.DoesNotContain(output.Content, c => c.Kind == ToolContentKind.Image);
        }

        [Theory]
        [InlineData("47494638376100", "GIF")]
        [InlineData("47494638396100", "GIF")]
        [InlineData("89504E470D0A1A0A", "PNG")]
        [InlineData("FFD8FFE000", "JPEG")]
        [InlineData("424D360000", "BMP")]
        [InlineData("D7CDC69A0000", "WMF")]
        [InlineData("0100090000", "WMF")]
        [InlineData("00010203", "unknown")]
        public void TheTypeComesFromTheFirstBytes(string hex, string kind)
        {
            byte[] bytes = Enumerable.Range(0, hex.Length / 2).Select(i => Convert.ToByte(hex.Substring(i * 2, 2), 16)).ToArray();
            Assert.Equal(kind, BinaryTools.Kind(bytes));
        }

        // ---- visa_query on a binary reply -----------------------------------------

        [Fact]
        public void VisaQuery_OnABinaryReply_PointsToVisaQueryBinary_RatherThanReturningMojibake()
        {
            var visa = new FakeInstrumentManager();
            visa.QueryResponses[":HCOP:SDUM:DATA? GIF"] = Encoding.GetEncoding("ISO-8859-1").GetString(Block(Gif));

            ToolOutput output = Tool("visa_query", visa).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["command"] = ":HCOP:SDUM:DATA? GIF"
            });

            Assert.True(output.IsError);
            Assert.Contains("visa_query_binary", output.AsText());
            Assert.Contains("header #3877", output.AsText());
        }

        [Theory]
        [InlineData("+0,\"No error\"")]
        [InlineData("#5 is a channel name")]
        [InlineData("-7.42E+001,-7.59E+001,-7.61E+001")]
        public void VisaQuery_TextReplies_AreNotMistakenForBinary(string reply)
        {
            Assert.False(BinaryTools.LooksBinary(reply, out _));
        }

        // ---- the E4406A's screen dump ----------------------------------------------

        private static (InstrumentDatabase, AssignmentStore) E4406A()
        {
            var def = new InstrumentDefinition
            {
                Model = "E4406A",
                Capture = new CaptureProfile
                {
                    Method = "scpi_block",
                    DumpCommand = ":HCOP:SDUM:DATA? GIF",
                    Invert = new CaptureInvertSpec { Query = ":HCOP:SDUM:IMAG?", On = ":HCOP:SDUM:IMAG INV", Off = ":HCOP:SDUM:IMAG NORM", OnReply = "INV" }
                }
            };
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::17::INSTR", "E4406A");
            return (InstrumentDatabase.FromDefinitions(new[] { def }), store);
        }

        [Fact]
        public void Capture_E4406A_ReadsItsGifBlock_AndReturnsPng()
        {
            var (db, store) = E4406A();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(Gif) };

            ToolOutput output = Tool("instrument_capture_screen", visa, db, store).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["format"] = "image", ["save_dir"] = _dir
            });

            Assert.False(output.IsError, output.AsText());
            Assert.Equal("GPIB0::17::INSTR|:HCOP:SDUM:DATA? GIF|Ieee4882", Assert.Single(visa.BinaryQueries));
            Assert.Contains(output.Content, c => c.Kind == ToolContentKind.Image && c.MimeType == "image/png");
            Assert.DoesNotContain("<svg", output.AsText());                                   // 'image': just the PNG
            Assert.Single(Directory.GetFiles(_dir, "*.gif"));                                // the original beside the PNG
            Assert.Single(Directory.GetFiles(_dir, "*.png"));
        }

        [Fact]
        public void Capture_ALargeScreen_IsSavedFullSize_AndOnlyTheAttachedCopyIsScaledDown()
        {
            var (db, store) = E4406A();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(NoiseBmp(800, 600)) };

            ToolOutput output = Tool("instrument_capture_screen", visa, db, store).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["format"] = "image", ["save_dir"] = _dir
            });

            Assert.False(output.IsError, output.AsText());
            Hpgl.Rendering.ScreenImage.Dimensions(File.ReadAllBytes(Directory.GetFiles(_dir, "*.png").Single()), out int w, out _);
            Assert.Equal(800, w);
            AssertAttachedImageIsScaledDown(output);
        }

        [Fact]
        public void Capture_FormatFile_OnlySaves()
        {
            var (db, store) = E4406A();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(Gif) };

            ToolOutput output = Tool("instrument_capture_screen", visa, db, store).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["format"] = "file", ["save_dir"] = _dir
            });

            Assert.DoesNotContain(output.Content, c => c.Kind == ToolContentKind.Image);
            Assert.Contains(".png", output.AsText());
            Assert.Contains("Original GIF", output.AsText());
        }

        [Fact]
        public void Capture_Invert_SetsItForTheCapture_ThenPutsTheUsersSettingBack()
        {
            var (db, store) = E4406A();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(Gif) };
            visa.QueryResponses[":HCOP:SDUM:IMAG?"] = "NORM";

            Tool("instrument_capture_screen", visa, db, store).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["format"] = "file", ["invert"] = true, ["save_dir"] = _dir
            });

            Assert.Equal(new[] { "GPIB0::17::INSTR|:HCOP:SDUM:IMAG INV", "GPIB0::17::INSTR|:HCOP:SDUM:IMAG NORM" }, visa.Writes);
        }

        [Fact]
        public void Capture_Invert_AlreadyAsAsked_ChangesNothing()
        {
            var (db, store) = E4406A();
            var visa = new FakeInstrumentManager { BinaryResponse = Block(Gif) };
            visa.QueryResponses[":HCOP:SDUM:IMAG?"] = "INV";

            Tool("instrument_capture_screen", visa, db, store).Invoke(new JObject
            {
                ["resource"] = "GPIB0::17::INSTR", ["format"] = "file", ["invert"] = true, ["save_dir"] = _dir
            });

            Assert.Empty(visa.Writes);
        }

        [Fact]
        public void TheBundledE4406A_HasAGifScreenDumpProfile_WithInvert()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "data", "instruments"))) dir = dir.Parent;
            Assert.NotNull(dir);
            var db = InstrumentDatabase.Load(new[] { Path.Combine(dir.FullName, "data", "instruments") });

            Assert.True(db.TryGet("E4406A", out var e4406a));
            Assert.Equal("scpi_block", e4406a.Capture.Method);
            Assert.Equal(":HCOP:SDUM:DATA? GIF", e4406a.Capture.DumpCommand);
            Assert.Equal(":HCOP:SDUM:IMAG INV", e4406a.Capture.Invert.On);
        }
    }
}
