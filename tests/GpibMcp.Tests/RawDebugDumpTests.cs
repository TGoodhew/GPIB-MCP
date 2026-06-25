using System;
using System.IO;
using GpibMcp.Diagnostics;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>#79 (debug slice): a debug=true dump writes the verbatim bytes to disk, control bytes and all,
    /// so the exact captured/written stream can be inspected to diagnose plot/render glitches.</summary>
    public class RawDebugDumpTests
    {
        private static string TempDir() =>
            Path.Combine(Path.GetTempPath(), "gpibmcp_debug_" + Path.GetRandomFileName());

        [Fact]
        public void Save_WritesVerbatimBytes_IncludingControlBytes_AndReturnsThePath()
        {
            string dir = TempDir();
            string prev = Environment.GetEnvironmentVariable("GPIB_MCP_DEBUG_DIR");
            Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", dir);
            try
            {
                byte[] data = { (byte)'L', (byte)'B', 0x03, (byte)'A', 0x00, 0x0E, 0x0F };   // LB...ETX + control bytes

                string path = RawDebugDump.Save("8563E-capture", "hpgl", data, timestamp: "20260624-180000-000");

                Assert.NotNull(path);
                Assert.Equal(Path.Combine(dir, "8563E-capture-20260624-180000-000.hpgl"), path);
                Assert.Equal(data, File.ReadAllBytes(path));   // byte-for-byte, ETX/NUL/SO/SI intact
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", prev);
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Save_SanitizesTheResourceIntoTheFilename()
        {
            string dir = TempDir();
            string prev = Environment.GetEnvironmentVariable("GPIB_MCP_DEBUG_DIR");
            Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", dir);
            try
            {
                // a VISA resource has ':' which is illegal in a filename - must be sanitized, not throw.
                string path = RawDebugDump.Save("writeraw-GPIB0::6::INSTR", "hpgl", new byte[] { 1, 2, 3 },
                    timestamp: "20260624-180000-000");

                Assert.NotNull(path);
                Assert.DoesNotContain(":", Path.GetFileName(path));
                Assert.True(File.Exists(path));
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_DEBUG_DIR", prev);
                try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
            }
        }
    }
}
