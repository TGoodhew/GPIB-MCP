using System;
using System.IO;
using GpibMcp.Diagnostics;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>#79: server-side retention of a capture's forwardable bytes (handle + prune).</summary>
    [Collection("CaptureFiles")]
    public class CaptureStoreTests
    {
        private static string FreshDir() =>
            Path.Combine(Path.GetTempPath(), "gpibmcp_capstore_" + Path.GetRandomFileName());

        private static void WithCapturesDir(string dir, Action body)
        {
            string prev = Environment.GetEnvironmentVariable("GPIB_MCP_CAPTURES_DIR");
            Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURES_DIR", dir);
            try { body(); }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_CAPTURES_DIR", prev);
                try { Directory.Delete(dir, true); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void Save_WritesBytesVerbatim_AndReturnsHandlePath()
        {
            string dir = FreshDir();
            WithCapturesDir(dir, () =>
            {
                byte[] payload = { (byte)'P', (byte)'D', 0x03, 0x00, 0x0E };   // includes control bytes
                string path = CaptureStore.Save("8563E", "hpgl", payload, "20260625-120000-000");

                Assert.NotNull(path);
                Assert.True(File.Exists(path));
                Assert.Equal(payload, File.ReadAllBytes(path));               // byte-for-byte
                Assert.Equal(dir, Path.GetDirectoryName(path));
                Assert.EndsWith(".hpgl", path);
                Assert.Contains("capture-8563E-", Path.GetFileName(path));
            });
        }

        [Fact]
        public void Save_PrunesToKeepLast_NewestSurvive()
        {
            string dir = FreshDir();
            WithCapturesDir(dir, () =>
            {
                int total = CaptureStore.KeepLast + 5;
                string newest = null, oldest = null;
                for (int i = 0; i < total; i++)
                {
                    string p = CaptureStore.Save("m", "hpgl", new byte[] { (byte)i },
                                                 "20260625-1200" + i.ToString("00") + "-000");
                    if (i == 0) oldest = p;
                    newest = p;
                }

                Assert.Equal(CaptureStore.KeepLast, Directory.GetFiles(dir, "capture-*.*").Length);
                Assert.True(File.Exists(newest));    // most recent kept
                Assert.False(File.Exists(oldest));   // oldest pruned
            });
        }
    }
}
