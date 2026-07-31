using System;
using GpibMcp.Diagnostics;
using GpibMcp.Tools;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The transport-agnostic core of the Model Context Protocol server. Turns one JSON-RPC 2.0 message into
    /// the response to send back; it never touches stdin/stdout/sockets - a <see cref="IMcpTransport"/>
    /// carries the bytes. The instrument layer (and the rest of the codebase) sits behind the tool registry
    /// and is likewise unaware of the transport.
    ///
    /// The server is single-threaded by design: tool calls run synchronously and the instrument backend
    /// serializes hardware access. <see cref="Dispatch"/> takes a lock so a concurrent transport (e.g. HTTP)
    /// cannot interleave two requests - the synchronous guarantee lives here, once, for every transport.
    /// </summary>
    public sealed class McpDispatcher : IMcpDispatcher
    {
        /// <summary>MCP revision this server implements, and the one it answers with by default.</summary>
        public const string ProtocolVersion = "2025-06-18";

        /// <summary>
        /// Revisions this server can actually speak, newest first. We negotiate against this set rather than
        /// echoing whatever the client asks for: from 2026-07-28 the wire format changes substantially
        /// (stateless dispatch, a required <c>resultType</c> on every result, no sessions), so agreeing to a
        /// revision we do not implement would produce responses the client is entitled to reject (#104).
        ///
        /// The older entries are here because our surface is tools-only - no roots, sampling, logging or
        /// elicitation - and that subset is unchanged across these revisions, so a client on one of them gets
        /// exactly the protocol it expects. New revisions join the set only once the code implements them.
        /// </summary>
        public static readonly string[] SupportedProtocolVersions =
        {
            "2025-06-18",
            "2025-03-26",
            "2024-11-05"
        };

        /// <summary>True when <paramref name="version"/> is a revision this server can speak.</summary>
        public static bool IsSupportedProtocolVersion(string version)
        {
            return !string.IsNullOrEmpty(version) &&
                   Array.IndexOf(SupportedProtocolVersions, version) >= 0;
        }

        /// <summary>Server name reported to clients during initialize.</summary>
        public const string ServerName = "gpib-mcp";

        /// <summary>Server version reported to clients during initialize.</summary>
        public const string ServerVersion = "0.2.0";

        private readonly ToolRegistry _tools;
        private readonly string _instructions;
        private readonly BatchLoopNudge _loopNudge;
        private readonly object _gate = new object();

        public McpDispatcher(ToolRegistry tools, string instructions = null, BatchLoopNudge loopNudge = null)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _instructions = instructions;
            _loopNudge = loopNudge ?? new BatchLoopNudge();
        }

        /// <inheritdoc/>
        public JObject Dispatch(JObject message)
        {
            // Serialize: preserve the single-threaded model regardless of how many requests a transport
            // delivers concurrently (the instrument bus and the loop-nudge counter are not re-entrant).
            lock (_gate) { return DispatchCore(message); }
        }

        private JObject DispatchCore(JObject message)
        {
            JToken id = message["id"];
            string method = (string)message["method"];

            // No method => this is a response to a server-initiated request. We send none, so ignore it.
            if (method == null) return null;

            bool isNotification = (id == null);
            var prms = message["params"] as JObject;

            try
            {
                if (isNotification)
                {
                    HandleNotification(method, prms);
                    return null;
                }

                JToken result = HandleRequest(method, prms);
                return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result ?? new JObject() };
            }
            catch (McpError mcp)
            {
                Log.Warn("Request '" + method + "' failed: " + mcp.Message);
                return ErrorEnvelope(id, mcp.Code, mcp.Message, mcp.ErrorData);
            }
            catch (Exception ex)
            {
                Log.Error("Unhandled error in '" + method + "'", ex);
                return ErrorEnvelope(id, -32603, "Internal error: " + ex.Message, null);
            }
        }

        private static JObject ErrorEnvelope(JToken id, int code, string message, JToken data)
        {
            var error = new JObject { ["code"] = code, ["message"] = message };
            if (data != null) error["data"] = data;
            return new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = error };
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
                    Log.Info("Client initialized.");
                    break;
                case "notifications/cancelled":
                    // Single-threaded synchronous server: nothing to cancel.
                    break;
                default:
                    Log.Debug("Ignoring notification: " + method);
                    break;
            }
        }

        private JObject BuildInitializeResult(JObject prms)
        {
            string clientProtocol = prms != null ? (string)prms["protocolVersion"] : null;

            var clientInfo = prms != null ? prms["clientInfo"] as JObject : null;
            string clientName = clientInfo != null ? (string)clientInfo["name"] : "unknown";
            Log.Info("initialize from client '" + clientName + "' (protocol " +
                     (clientProtocol ?? "unspecified") + ")");

            // Answer with the client's revision only when we implement it; otherwise name the newest one we
            // do, which is what the spec asks of a server that cannot meet the request. The client then
            // decides whether to continue or disconnect - far better than us claiming a revision we cannot
            // speak (#104).
            string negotiated = ProtocolVersion;
            if (IsSupportedProtocolVersion(clientProtocol))
            {
                negotiated = clientProtocol;
            }
            else if (!string.IsNullOrEmpty(clientProtocol))
            {
                Log.Warn("Client requested unsupported MCP protocol '" + clientProtocol +
                         "'; answering with " + ProtocolVersion + ".");
            }

            var result = new JObject
            {
                ["protocolVersion"] = negotiated,
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
            // MCP spec: optional high-level guidance the client loads up front so the model can answer
            // capability questions accurately (issue #36).
            if (!string.IsNullOrEmpty(_instructions)) result["instructions"] = _instructions;
            return result;
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

            Log.Debug("tools/call '" + name + "' args=" + arguments.ToString(Formatting.None));
            // One always-on audit line per call (level-independent), so a whole turn can be reconstructed
            // afterwards - e.g. count single-op calls vs one gpib_batch, total non-batched time (#74 insight).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ToolOutput output = tool.Invoke(arguments);
                watch.Stop();
                ToolCallLog.Write(name, arguments, !output.IsError, watch.ElapsedMilliseconds);
                // #74: if the model is grinding through a per-point loop single-op, append a nudge to switch
                // to gpib_batch - here, in the result it actually reads (soft steering alone didn't land).
                string nudge = _loopNudge.Observe(name);
                if (nudge != null) output.AddText(nudge);
                return ToToolResult(output);
            }
            catch (Exception ex)
            {
                // Tool execution failures are reported as a normal result with isError=true,
                // per the MCP spec, so the model can see and react to the error text. When the
                // exception carries richer detail (e.g. a GPIB/VISA failure with decoded status
                // and the command chain), surface that so the model can explain it to the user.
                watch.Stop();
                ToolCallLog.Write(name, arguments, false, watch.ElapsedMilliseconds);
                _loopNudge.Observe(name);   // count the (failed) single-op call so the run length stays accurate
                Log.Warn("Tool '" + name + "' failed: " + ex.Message);
                string text = (ex is IDetailedError detailed) ? detailed.Detail : ex.Message;
                return ToToolResult(ToolOutput.Text("Error: " + text).AsError());
            }
        }

        private static JObject ToToolResult(ToolOutput output)
        {
            var content = new JArray();
            foreach (var block in output.Content)
            {
                if (block.Kind == ToolContentKind.Image)
                    content.Add(new JObject
                    {
                        ["type"] = "image",
                        ["data"] = block.Data,
                        ["mimeType"] = block.MimeType
                    });
                else
                    content.Add(new JObject { ["type"] = "text", ["text"] = block.Text ?? string.Empty });
            }

            var result = new JObject { ["content"] = content };
            if (output.IsError) result["isError"] = true;
            return result;
        }
    }
}
