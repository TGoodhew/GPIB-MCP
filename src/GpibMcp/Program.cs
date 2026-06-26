using System;
using System.IO;
using System.Text;
using GpibMcp.Diagnostics;
using GpibMcp.Http;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Stdio;
using GpibMcp.Tools;

namespace GpibMcp
{
    /// <summary>
    /// Process entry point and composition root. Builds the instrument layer and the transport-agnostic
    /// <see cref="McpDispatcher"/>, selects a transport module (stdio by default, or Streamable HTTP), and
    /// runs it until the client disconnects / the server is shut down. The core and the transports are
    /// separate modules wired together only here (#68).
    /// </summary>
    internal static class Program
    {
        private static int Main(string[] args)
        {
            Log.Info("starting " + McpDispatcher.ServerName + " " + McpDispatcher.ServerVersion +
                     " (MCP " + McpDispatcher.ProtocolVersion + ", log level " + Log.MinimumLevel + ")");

            InstrumentManager visa = null;
            try
            {
                string exeDir = Path.GetDirectoryName(typeof(Program).Assembly.Location);
                InstrumentPaths.EnsureUserDatabaseSeeded(exeDir);
                var db = InstrumentDatabase.Load(InstrumentPaths.DatabaseDirectories(exeDir));
                var assignments = AssignmentStore.FromFile(InstrumentPaths.BindingsPath());

                visa = new InstrumentManager(TransportFactory.Create());
                ToolRegistry registry = InstrumentTools.BuildRegistry(visa, db, assignments);
                string instructions = new ServerOverview(registry, db).Instructions();

                var dispatcher = new McpDispatcher(registry, instructions);
                using (IMcpTransport transport = CreateTransport())
                {
                    transport.Run(dispatcher);
                }
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

        /// <summary>
        /// Selects the MCP transport from the environment: <c>GPIB_MCP_TRANSPORT=http</c> serves Streamable
        /// HTTP (host <c>GPIB_MCP_HTTP_HOST</c> default 127.0.0.1, port <c>GPIB_MCP_HTTP_PORT</c> default 3001,
        /// optional bearer <c>GPIB_MCP_HTTP_TOKEN</c>); anything else (the default) uses stdio.
        /// </summary>
        private static IMcpTransport CreateTransport()
        {
            string kind = (Environment.GetEnvironmentVariable("GPIB_MCP_TRANSPORT") ?? "stdio").Trim().ToLowerInvariant();
            if (kind == "http")
            {
                string host = Environment.GetEnvironmentVariable("GPIB_MCP_HTTP_HOST");
                string token = Environment.GetEnvironmentVariable("GPIB_MCP_HTTP_TOKEN");
                int port = int.TryParse(Environment.GetEnvironmentVariable("GPIB_MCP_HTTP_PORT"), out int p) ? p : 3001;
                return new HttpTransport(host, port, token);
            }

            // stdio (default): UTF-8, newline-delimited JSON. BOM-less so the first frame isn't corrupted;
            // stdout is reserved for protocol data, diagnostics go to stderr.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
            var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false };
            return new StdioTransport(stdin, stdout);
        }
    }
}
