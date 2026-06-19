using System;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// Minimal Model Context Protocol server speaking JSON-RPC 2.0 over a
    /// newline-delimited stdio transport (one JSON object per line).
    ///
    /// stdout carries protocol traffic ONLY; all diagnostics go to stderr.
    /// </summary>
    public sealed class McpServer
    {
        /// <summary>MCP revision this server implements (echoed back if the client requests it).</summary>
        public const string ProtocolVersion = "2025-06-18";
        public const string ServerName = "gpib-mcp";
        public const string ServerVersion = "0.1.0";

        private readonly ToolRegistry _tools;
        private readonly TextReader _input;
        private readonly TextWriter _output;
        private readonly object _writeGate = new object();

        public McpServer(ToolRegistry tools, TextReader input, TextWriter output)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _input = input ?? throw new ArgumentNullException(nameof(input));
            _output = output ?? throw new ArgumentNullException(nameof(output));
        }

        /// <summary>Blocking read loop. Returns when stdin reaches EOF (client disconnected).</summary>
        public void Run()
        {
            string line;
            while ((line = _input.ReadLine()) != null)
            {
                if (line.Length == 0) continue;
                JObject message;
                try
                {
                    message = JObject.Parse(line);
                }
                catch (Exception ex)
                {
                    Log("Ignoring unparseable line: " + ex.Message);
                    continue;
                }
                Dispatch(message);
            }
            Log("stdin closed; shutting down.");
        }

        private void Dispatch(JObject message)
        {
            JToken id = message["id"];
            string method = (string)message["method"];

            // No method => this is a response to a server-initiated request. We send none, so ignore.
            if (method == null) return;

            bool isNotification = (id == null);
            var prms = message["params"] as JObject;

            try
            {
                if (isNotification)
                {
                    HandleNotification(method, prms);
                    return;
                }

                JToken result = HandleRequest(method, prms);
                SendResult(id, result);
            }
            catch (McpError mcp)
            {
                SendError(id, mcp.Code, mcp.Message, mcp.ErrorData);
            }
            catch (Exception ex)
            {
                Log("Unhandled error in " + method + ": " + ex);
                SendError(id, -32603, "Internal error: " + ex.Message, null);
            }
        }

        private JToken HandleRequest(string method, JObject prms)
        {
            switch (method)
            {
                case "initialize":
                    return BuildInitializeResult(prms);

                case "ping":
                    return new JObject();

                case "tools/list":
                    return new JObject { ["tools"] = _tools.ToListJson() };

                case "tools/call":
                    return CallTool(prms);

                default:
                    throw McpError.MethodNotFound(method);
            }
        }

        private void HandleNotification(string method, JObject prms)
        {
            switch (method)
            {
                case "notifications/initialized":
                    Log("Client initialized.");
                    break;
                case "notifications/cancelled":
                    // Single-threaded synchronous server: nothing to cancel.
                    break;
                default:
                    Log("Ignoring notification: " + method);
                    break;
            }
        }

        private JObject BuildInitializeResult(JObject prms)
        {
            // Echo the client's protocol version when present for best compatibility.
            string clientProtocol = prms != null ? (string)prms["protocolVersion"] : null;

            return new JObject
            {
                ["protocolVersion"] = string.IsNullOrEmpty(clientProtocol) ? ProtocolVersion : clientProtocol,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false }
                },
                ["serverInfo"] = new JObject
                {
                    ["name"] = ServerName,
                    ["version"] = ServerVersion
                }
            };
        }

        private JObject CallTool(JObject prms)
        {
            if (prms == null) throw McpError.InvalidParams("missing params");
            string name = (string)prms["name"];
            if (string.IsNullOrEmpty(name)) throw McpError.InvalidParams("missing tool name");

            var arguments = prms["arguments"] as JObject ?? new JObject();

            McpTool tool;
            if (!_tools.TryGet(name, out tool))
                throw McpError.InvalidParams("unknown tool: " + name);

            try
            {
                string text = tool.Invoke(arguments);
                return ToolResult(text, false);
            }
            catch (Exception ex)
            {
                // Tool execution failures are reported as a normal result with isError=true,
                // per the MCP spec, so the model can see and react to the error text.
                Log("Tool '" + name + "' failed: " + ex.Message);
                return ToolResult("Error: " + ex.Message, true);
            }
        }

        private static JObject ToolResult(string text, bool isError)
        {
            var result = new JObject
            {
                ["content"] = new JArray
                {
                    new JObject { ["type"] = "text", ["text"] = text ?? string.Empty }
                }
            };
            if (isError) result["isError"] = true;
            return result;
        }

        private void SendResult(JToken id, JToken result)
        {
            Write(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["result"] = result ?? new JObject()
            });
        }

        private void SendError(JToken id, int code, string message, JToken data)
        {
            var error = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) error["data"] = data;
            Write(new JObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["error"] = error
            });
        }

        private void Write(JObject payload)
        {
            string json = payload.ToString(Formatting.None);
            lock (_writeGate)
            {
                _output.Write(json);
                _output.Write('\n');
                _output.Flush();
            }
        }

        private static void Log(string message) => Console.Error.WriteLine("[gpib-mcp] " + message);
    }
}
