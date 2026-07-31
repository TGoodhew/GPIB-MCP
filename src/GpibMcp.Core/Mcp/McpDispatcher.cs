using System;
using GpibMcp.Diagnostics;
using GpibMcp.Mcp.Tasks;
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
    /// The server is single-threaded where it matters: every tool invocation - foreground or task-backed -
    /// takes one lock, so a concurrent transport (e.g. HTTP) can never put two requests on the GPIB bus at
    /// once. The lock deliberately does <b>not</b> cover the whole of <see cref="Dispatch"/>: a
    /// <c>tasks/get</c> poll must be answerable while a 24-second capture is still on the hardware, which is
    /// the entire point of the tasks extension (#112). Everything outside tool execution is read-only state.
    /// </summary>
    public sealed class McpDispatcher : IMcpDispatcher, IDisposable
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

        /// <summary>
        /// Identifier of the tasks extension (SEP-2663). A client declares support for it in its capabilities
        /// and the server advertises the same key; only then may we answer a request with a task handle.
        /// </summary>
        public const string TasksExtension = "io.modelcontextprotocol/tasks";

        private readonly ToolRegistry _tools;
        private readonly string _instructions;
        private readonly BatchLoopNudge _loopNudge;
        private readonly object _toolGate = new object();
        private readonly TaskStore _taskStore = new TaskStore();
        private readonly Lazy<TaskRunner> _taskRunner = new Lazy<TaskRunner>(() => new TaskRunner());

        /// <summary>Set at initialize when the client declares the tasks extension (the 2025-06-18 route).</summary>
        private volatile bool _clientDeclaredTasks;

        public McpDispatcher(ToolRegistry tools, string instructions = null, BatchLoopNudge loopNudge = null)
        {
            _tools = tools ?? throw new ArgumentNullException(nameof(tools));
            _instructions = instructions;
            _loopNudge = loopNudge ?? new BatchLoopNudge();
        }

        /// <inheritdoc/>
        public IMcpMessageSink Notifications { get; set; }

        /// <summary>The live tasks this server has handed out (exposed for diagnostics and tests).</summary>
        public TaskStore Tasks => _taskStore;

        /// <inheritdoc/>
        public JObject Dispatch(JObject message)
        {
            return DispatchCore(message);
        }

        private JObject DispatchCore(JObject message)
        {
            JToken id = message["id"];
            string method = (string)message["method"];

            // No method => this is a response to a server-initiated request. We send none, so ignore it.
            if (method == null) return null;

            bool isNotification = (id == null);
            var prms = message["params"] as JObject;

            // Read the request's own context once (#106). From 2026-07-28 there is no handshake, so this is
            // where the client says what revision it speaks, what it supports and who it is; a 2025-06-18
            // client sends none of it and the values it gave at initialize still stand.
            var context = new RequestContext(prms != null ? prms["_meta"] as JObject : null);
            if (context.ProtocolVersion != null && !IsSupportedProtocolVersion(context.ProtocolVersion))
                Log.Warn("Request '" + method + "' declares MCP protocol '" + context.ProtocolVersion +
                         "', which this server does not implement; answering as " + ProtocolVersion + ".");
            if (context.ClientName != null)
                Log.Debug("Request '" + method + "' from client '" + context.ClientName + "'.");
            if (context.LogLevel != null)
                // Honouring this would mean notifications/message, which we deliberately never emit: every
                // diagnostic goes to stderr instead - the migration the spec recommends for deprecated Logging.
                Log.Debug("Client asked for log level '" + context.LogLevel + "'; diagnostics go to stderr.");

            try
            {
                if (isNotification)
                {
                    HandleNotification(method, prms);
                    return null;
                }

                JToken result = HandleRequest(method, prms, context);
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = WithServerInfo(result as JObject ?? new JObject())
                };
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

        /// <summary>
        /// Names this server in the result's <c>_meta</c> (#106). Without a handshake a client would
        /// otherwise never learn who answered, so every result says so - and it is additive, which is why it
        /// can go to a 2025-06-18 client too: <c>_meta</c> has always been the ignorable extension point.
        /// </summary>
        private static JObject WithServerInfo(JObject result)
        {
            var meta = result["_meta"] as JObject;
            if (meta == null)
            {
                meta = new JObject();
                result["_meta"] = meta;
            }
            meta[RequestContext.ServerInfoKey] = new JObject
            {
                ["name"] = ServerName,
                ["version"] = ServerVersion
            };
            return result;
        }

        private JToken HandleRequest(string method, JObject prms, RequestContext context)
        {
            switch (method)
            {
                // initialize / notifications/initialized / ping are the 2025-06-18 handshake, which
                // 2026-07-28 removes. They stay: every client we ship for still speaks that revision, and a
                // server has to serve both for the whole deprecation window (#106).
                case "initialize":
                    return BuildInitializeResult(prms);

                case "ping":
                    return new JObject();

                case "tools/list":
                    return new JObject { ["tools"] = _tools.ToListJson() };

                case "tools/call":
                    return CallTool(prms, context);

                // io.modelcontextprotocol/tasks (#112). Served unconditionally: a client only ever holds a
                // task id because we gave it one, and answering a poll is never the wrong thing to do.
                case "tasks/get":
                    return FindTask(prms).ToDetailedResult();

                case "tasks/cancel":
                    return CancelTask(prms);

                case "tasks/update":
                    return UpdateTask(prms);

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

            // Tasks are opt-in from both sides: remember whether this client declared the extension, because
            // the spec is explicit that a server must never hand a task to a client that did not (#112).
            var clientCaps = prms != null ? prms["capabilities"] as JObject : null;
            _clientDeclaredTasks = DeclaresTasks(clientCaps);
            if (_clientDeclaredTasks)
                Log.Info("Client supports " + TasksExtension + "; long-running calls may return a task handle.");

            var result = new JObject
            {
                ["protocolVersion"] = negotiated,
                ["capabilities"] = new JObject
                {
                    ["tools"] = new JObject { ["listChanged"] = false },
                    // ServerCapabilities.extensions (2026-07-28 minor change 1). Advertised to every client:
                    // one that predates the field ignores it, and it is how a client learns we can do this
                    // before server/discover exists here (#105).
                    ["extensions"] = new JObject { [TasksExtension] = new JObject() }
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

        private JObject CallTool(JObject prms, RequestContext context)
        {
            if (prms == null) throw McpError.InvalidParams("missing params");
            string name = (string)prms["name"];
            if (string.IsNullOrEmpty(name)) throw McpError.InvalidParams("missing tool name");

            var arguments = prms["arguments"] as JObject ?? new JObject();

            McpTool tool;
            if (!_tools.TryGet(name, out tool))
                throw McpError.InvalidParams("unknown tool: " + name);

            Log.Debug("tools/call '" + name + "' args=" + arguments.ToString(Formatting.None));

            // A slow tool becomes a task only when the client has said it can handle one. Everything else -
            // every fast tool, and every client that has not opted in - runs exactly as it always has (#112).
            if (tool.LongRunning && ClientSupportsTasks(context))
                return StartTaskCall(tool, name, arguments);

            return ExecuteTool(tool, name, arguments, ProgressContext(context));
        }

        /// <summary>
        /// Runs a tool to completion and shapes the <c>tools/call</c> result. Holds the tool lock throughout,
        /// so this is the single point where hardware access is serialized - foreground calls and the task
        /// runner alike. The lock covers the audit log and loop-nudge counter too, neither of which is
        /// re-entrant; polling a task takes no lock at all, which is what keeps it answerable meanwhile.
        /// </summary>
        private JObject ExecuteTool(McpTool tool, string name, JObject arguments, ToolCallContext context)
        {
            lock (_toolGate) { return ExecuteToolCore(tool, name, arguments, context); }
        }

        private JObject ExecuteToolCore(McpTool tool, string name, JObject arguments, ToolCallContext context)
        {
            // One always-on audit line per call (level-independent), so a whole turn can be reconstructed
            // afterwards - e.g. count single-op calls vs one gpib_batch, total non-batched time (#74 insight).
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                ToolOutput output = tool.Invoke(arguments, context);
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

        /// <summary>
        /// Hands the call to the task runner and answers immediately with a <c>CreateTaskResult</c>. The task
        /// is registered before we reply, so a client that polls the instant it sees the id always finds it.
        /// </summary>
        private JObject StartTaskCall(McpTool tool, string name, JObject arguments)
        {
            ServerTask task = _taskStore.Create("tools/call " + name);
            task.SetStatusMessage("Queued: " + name + ".");

            // While a task runs there is no open request to attach progress to, so the milestones a tool
            // reports become the task's statusMessage - which is exactly what the client's next poll reads.
            var context = new ToolCallContext((progress, total, message) => task.SetStatusMessage(message));

            _taskRunner.Value.Enqueue(task, () =>
            {
                task.SetStatusMessage("Running " + name + ".");
                return ExecuteTool(tool, name, arguments, context);
            });

            Log.Info("tools/call '" + name + "' running as task " + task.TaskId + ".");
            return task.ToCreateResult();
        }

        /// <summary>
        /// Builds the context for a synchronous call: progress goes out as <c>notifications/progress</c> when
        /// the client asked for it with a <c>_meta.progressToken</c> and the transport can carry one.
        /// </summary>
        private ToolCallContext ProgressContext(RequestContext context)
        {
            JToken token = context.ProgressToken;
            IMcpMessageSink sink = Notifications;
            if (token == null || sink == null) return ToolCallContext.None;

            return new ToolCallContext((progress, total, message) =>
            {
                var notificationParams = new JObject
                {
                    ["progressToken"] = token.DeepClone(),
                    ["progress"] = progress
                };
                if (total.HasValue) notificationParams["total"] = total.Value;
                if (!string.IsNullOrEmpty(message)) notificationParams["message"] = message;

                sink.Send(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/progress",
                    ["params"] = notificationParams
                });
            });
        }

        /// <summary>
        /// True when this client can be handed a task: either it declared the extension at initialize
        /// (2025-06-18 style) or it carried the declaration in this request's <c>_meta</c>, which is how
        /// 2026-07-28 negotiates now that there is no handshake.
        /// </summary>
        private bool ClientSupportsTasks(RequestContext context)
        {
            return _clientDeclaredTasks || context.DeclaresExtension(TasksExtension);
        }

        private static bool DeclaresTasks(JObject capabilities)
        {
            var extensions = capabilities != null ? capabilities["extensions"] as JObject : null;
            return extensions != null && extensions[TasksExtension] != null;
        }

        private ServerTask FindTask(JObject prms)
        {
            string taskId = prms != null ? (string)prms["taskId"] : null;
            if (string.IsNullOrEmpty(taskId)) throw McpError.InvalidParams("missing taskId");

            ServerTask task;
            if (!_taskStore.TryGet(taskId, out task))
                throw McpError.InvalidParams("unknown task: " + taskId);
            return task;
        }

        private JObject CancelTask(JObject prms)
        {
            ServerTask task = FindTask(prms);
            task.RequestCancel();
            // Cooperative, and honest about it: a task still queued is cancelled by the runner before it
            // touches the bus, but one already mid-capture will finish - a blocking GPIB read cannot be
            // interrupted, and the extension allows a cancelled-but-completed outcome.
            Log.Info("tasks/cancel requested for " + task.TaskId + " (status " + task.Status + ").");
            return new JObject { ["resultType"] = "complete" };
        }

        private JObject UpdateTask(JObject prms)
        {
            ServerTask task = FindTask(prms);
            // We never move a task to input_required - no sampling, no elicitation - so there is nothing
            // outstanding for responses to satisfy. The extension says to acknowledge and ignore them.
            Log.Debug("tasks/update for " + task.TaskId + " ignored: no outstanding input requests.");
            return new JObject { ["resultType"] = "complete" };
        }

        /// <summary>Stops the task runner. Any queued work is settled rather than left pending.</summary>
        public void Dispose()
        {
            if (_taskRunner.IsValueCreated) _taskRunner.Value.Dispose();
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
            // The machine-readable twin of the text above (#113). Tools that declare an outputSchema set it
            // on every path they can - including their own failure envelopes - so a client validating against
            // the schema always has something to validate. A crash out of the handler is the exception: that
            // returns isError, which the spec exempts from output validation.
            if (output.Structured != null) result["structuredContent"] = output.Structured;
            if (output.IsError) result["isError"] = true;
            return result;
        }
    }
}
