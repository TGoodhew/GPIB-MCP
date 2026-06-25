using System;
using System.IO;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Printing;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #83: spool a captured hardcopy to a Windows printer. These tests never actually print - they only
    /// exercise the list path and the guard rails (missing file, unknown printer), which all bail before any
    /// spooler call, so they're safe on any machine regardless of which printers are installed.
    /// </summary>
    public class PrintToolsTests
    {
        private static McpTool Tool()
        {
            InstrumentTools.BuildRegistry(new FakeInstrumentManager()).TryGet("print_capture_to_windows", out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        [Fact]
        public void WindowsRawPrinter_Enumeration_DoesNotThrow()
        {
            Assert.NotNull(WindowsRawPrinter.InstalledPrinters());   // possibly empty (e.g. CI), but never null
            var ex = Record.Exception(() => WindowsRawPrinter.DefaultPrinter());
            Assert.Null(ex);                                         // default lookup is best-effort, never throws
        }

        [Fact]
        public void Tool_List_ReturnsPrintersOrNone_WithoutPrinting()
        {
            var output = Tool().Invoke(new JObject { ["list"] = true });
            Assert.False(output.IsError);
            Assert.Contains("printer", output.AsText().ToLowerInvariant());
        }

        [Fact]
        public void Tool_NoPathNoList_FallsBackToListing()
        {
            // With no path there is nothing to print, so it lists rather than erroring.
            var output = Tool().Invoke(new JObject());
            Assert.False(output.IsError);
            Assert.Contains("printer", output.AsText().ToLowerInvariant());
        }

        [Fact]
        public void Tool_MissingFile_Throws()
        {
            var missing = Path.Combine(Path.GetTempPath(), "no_such_" + Path.GetRandomFileName() + ".pcl");
            var args = new JObject { ["path"] = missing, ["printer"] = "__definitely_not_a_real_printer__" };
            Assert.Throws<ArgumentException>(() => Tool().Invoke(args));
        }

        [Fact]
        public void Tool_UnknownPrinter_Throws_WithoutPrinting()
        {
            // A real file but a bogus printer name: fails the installed-printer check before any spooling,
            // so this can't accidentally print to a real queue.
            string file = Path.Combine(Path.GetTempPath(), "pt_" + Path.GetRandomFileName() + ".pcl");
            File.WriteAllBytes(file, new byte[] { 0x1B, (byte)'E' });
            try
            {
                var args = new JObject { ["path"] = file, ["printer"] = "__definitely_not_a_real_printer__" };
                var ex = Assert.Throws<ArgumentException>(() => Tool().Invoke(args));
                Assert.Contains("No Windows printer named", ex.Message);
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }
}
