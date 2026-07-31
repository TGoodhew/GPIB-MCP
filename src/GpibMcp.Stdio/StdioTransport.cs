using System;
using System.IO;
using GpibMcp.Diagnostics;
using GpibMcp.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Stdio
{
    /// <summary>
    /// MCP transport over newline-delimited JSON-RPC on stdin/stdout (one JSON object per line). This is the
    /// transport MCP desktop clients launch as a child process. stdout carries protocol traffic ONLY; all
    /// diagnostics go to stderr (via <see cref="Log"/>). The module owns no protocol logic - it just frames
    /// bytes and defers to an <see cref="IMcpDispatcher"/>.
    ///
    /// It is also an <see cref="IMcpMessageSink"/>: the output stream stays open between responses, so the
    /// dispatcher can interleave <c>notifications/progress</c> while a slow tool call is still running (#112).
    /// Writes are gated, so a notification from the task runner cannot cut into a response mid-line.
    /// </summary>
    public sealed class StdioTransport : IMcpTransport, IMcpMessageSink
    {
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly object _writeGate = new object();

        public StdioTransport(TextReader input, TextWriter output)
        {
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>Blocking read loop. Returns when stdin reaches EOF (client disconnected).</summary>
        public void Run(IMcpDispatcher dispatcher)
        {
            if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
            dispatcher.Notifications = this;   // stdout can carry server→client messages; offer it

            string line;
            while ((line = _input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                Log.Debug("<- " + line);

                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (Exception ex)
                {
                    Log.Warn("Ignoring unparseable line: " + ex.Message);
                    continue;
                }

                JObject response = dispatcher.Dispatch(message);
                if (response != null) Write(response);
            }
            Log.Info("stdin closed; shutting down.");
        }

        /// <inheritdoc/>
        public void Send(JObject message)
        {
            if (message != null) Write(message);
        }

        private void Write(JObject payload)
        {
            string json = payload.ToString(Formatting.None);
            Log.Debug("-> " + json);
            lock (_writeGate)
            {
                _output.Write(json);
                _output.Write('\n');
                _output.Flush();
            }
        }

        public void Dispose() { /* nothing to release; the streams are owned by the caller */ }
    }
}
