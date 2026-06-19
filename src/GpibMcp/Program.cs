using System;
using System.IO;
using System.Text;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;

namespace GpibMcp
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            // MCP stdio transport is UTF-8, newline-delimited JSON. Use a BOM-less encoding
            // so the first frame is not corrupted, and keep stdout reserved for protocol data.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

            var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };

            Console.Error.WriteLine("[gpib-mcp] starting " + McpServer.ServerName + " " +
                                    McpServer.ServerVersion + " (MCP " + McpServer.ProtocolVersion + ")");

            using (var visa = new VisaInstrumentManager())
            {
                ToolRegistry registry = InstrumentTools.BuildRegistry(visa);
                var server = new McpServer(registry, stdin, stdout);
                try
                {
                    server.Run();
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("[gpib-mcp] fatal: " + ex);
                    return 1;
                }
            }
            return 0;
        }
    }
}
