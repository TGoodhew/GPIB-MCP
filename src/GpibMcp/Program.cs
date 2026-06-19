using System;
using System.IO;
using System.Text;
using GpibMcp.Diagnostics;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;

namespace GpibMcp
{
    /// <summary>
    /// Process entry point. Wires up the UTF-8 stdio transport, the instrument layer, and
    /// the MCP server, then runs the blocking request loop until the client disconnects.
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // MCP stdio transport is UTF-8, newline-delimited JSON. Use a BOM-less encoding
            // so the first frame is not corrupted, and keep stdout reserved for protocol data.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            using (var stdin = new StreamReader(Console.OpenStandardInput(), utf8))
            using (var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false })
            {
                Log.Info("starting " + McpServer.ServerName + " " + McpServer.ServerVersion +
                         " (MCP " + McpServer.ProtocolVersion + ", log level " + Log.MinimumLevel + ")");

                VisaInstrumentManager visa = null;
                try
                {
                    visa = new VisaInstrumentManager();
                    ToolRegistry registry = InstrumentTools.BuildRegistry(visa);
                    var server = new McpServer(registry, stdin, stdout);
                    server.Run();
                    return 0;
                }
                catch (Exception ex)
                {
                    Log.Error("fatal error; terminating", ex);
                    return 1;
                }
                finally
                {
                    visa?.Dispose();
                }
            }
        }
    }
}
