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

                CheckDeclaredProtocol(method, context);

                JToken result = HandleRequest(method, prms, context, id);
                return new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = WithResultType(WithServerInfo(result as JObject ?? new JObject()),
                                                context.DeclaresRevisionAtLeast(RequestContext.StatelessRevision))
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

        /// <summary>
        /// Checks the revision a request declares (#109). A dated revision we do not fully implement is
        /// still served, best-effort, and logged: we already answer much of 2026-07-28's shape
        /// (<c>resultType</c>, per-result <c>serverInfo</c>, <c>server/discover</c>, cache hints), and
        /// refusing it outright would put those features out of reach of the only clients that want them.
        /// Something that is not a revision at all we cannot serve on any reading, so it gets the
        /// specification's <c>UnsupportedProtocolVersionError</c> with the list we do speak.
        ///
        /// When 2026-07-28 is either finished or ruled out, this is where the best-effort branch turns into
        /// a refusal - the last step of the epic, not a step inside it.
        /// </summary>
        private static void CheckDeclaredProtocol(string method, RequestContext context)
        {
            string declared = context.ProtocolVersion;
            if (declared == null || IsSupportedProtocolVersion(declared)) return;

            if (!RequestContext.IsRevisionName(declared))
                throw McpError.UnsupportedProtocolVersion(declared, SupportedProtocolVersions);

            Log.Warn("Request '" + method + "' declares MCP protocol '" + declared +
                     "', which this server does not fully implement; answering as " + ProtocolVersion + ".");
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
            meta[RequestContext.ServerInfoKey] = ServerIdentity();
            return result;
        }

        /// <summary>
        /// Marks an ordinary result <c>complete</c> (#107, SEP-2322), which 2026-07-28 requires on every
        /// result. Only for a request that declares that revision: a client on an older one may validate
        /// strictly against a schema with no such field, and it is told to read an absent field as
        /// "complete" anyway, so adding it there would be risk without meaning.
        ///
        /// An existing value is never overwritten - a <c>CreateTaskResult</c> is <c>"task"</c>, and saying
        /// "complete" over the top of it would tell the client the work had finished when it has not (#112).
        /// </summary>
        private static JObject WithResultType(JObject result, bool wanted)
        {
            if (wanted && result["resultType"] == null) result["resultType"] = "complete";
            return result;
        }

        private JToken HandleRequest(string method, JObject prms, RequestContext context, JToken id)
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

                // Required from 2026-07-28, and the one method a client may call before anything else (#105).
                case "server/discover":
                    return BuildDiscoverResult();

                // The long-lived change-notification stream that replaced the HTTP GET endpoint (#111).
                case "subscriptions/listen":
                    return OpenAndCloseSubscription(id);

                case "tools/list":
                    return BuildToolsListResult(context);

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
                ["capabilities"] = BuildCapabilities(),
                ["serverInfo"] = ServerIdentity()
            };
            // MCP spec: optional high-level guidance the client loads up front so the model can answer
            // capability questions accurately (issue #36).
            if (!string.IsNullOrEmpty(_instructions)) result["instructions"] = _instructions;
            return result;
        }

        /// <summary>
        /// Answers <c>server/discover</c> (#105), which 2026-07-28 makes a MUST: the versions we speak, what
        /// we can do, and who we are, in one request and without a handshake. It doubles as the stdio
        /// backward-compatibility probe, which is why it is served whatever revision the caller is on.
        ///
        /// <c>supportedVersions</c> is the honest set from <see cref="SupportedProtocolVersions"/> - which
        /// does not include 2026-07-28, because we do not implement all of it yet. Implementing this method
        /// is not a claim to the revision that introduced it; a client reads the list and picks.
        /// </summary>
        private JObject BuildDiscoverResult()
        {
            var result = new JObject
            {
                ["supportedVersions"] = new JArray(SupportedProtocolVersions),
                ["capabilities"] = BuildCapabilities()
            };
            if (!string.IsNullOrEmpty(_instructions)) result["instructions"] = _instructions;
            // The identity goes in _meta here, not a top-level serverInfo - every result gets that already.
            return CacheableResult.ApplyTo(result);
        }

        /// <summary>
        /// Answers <c>subscriptions/listen</c> (#111): acknowledge, agreeing to nothing, then close the
        /// subscription gracefully.
        ///
        /// This server has nothing to subscribe to. The tool list is fixed at start-up - which is what
        /// <c>listChanged: false</c> says - and there are no resources or prompts to change. The
        /// acknowledgement's <c>notifications</c> field is the subset the server agreed to honour, so ours is
        /// empty, and a client reading it learns exactly that. Holding a stream open afterwards would promise
        /// a message that can never come, so the empty result follows immediately: that is the spec's
        /// graceful closure, and it is the difference between "ended cleanly" and a dropped connection.
        ///
        /// The acknowledgement needs an outbound channel. Stdio has one; the HTTP transport does not - a POST
        /// there gets one JSON response - so an HTTP caller receives the closure alone. Nothing is lost:
        /// with no notification types agreed, the two messages carry the same information.
        /// </summary>
        private JObject OpenAndCloseSubscription(JToken id)
        {
            JToken subscriptionId = id != null ? id.DeepClone() : JValue.CreateNull();

            IMcpMessageSink sink = Notifications;
            if (sink != null)
            {
                // MUST be the first message on the subscription - so it goes before the response below.
                sink.Send(new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["method"] = "notifications/subscriptions/acknowledged",
                    ["params"] = new JObject
                    {
                        ["_meta"] = new JObject { [RequestContext.SubscriptionIdKey] = subscriptionId.DeepClone() },
                        // Empty: we agreed to none of the types asked for, because we support none.
                        ["notifications"] = new JObject()
                    }
                });
            }

            Log.Debug("subscriptions/listen: nothing is subscribable here; acknowledged and closed.");
            return new JObject
            {
                ["_meta"] = new JObject { [RequestContext.SubscriptionIdKey] = subscriptionId }
            };
        }

        /// <summary>
        /// The tool catalogue (#108). Order is the registry's registration order and is deliberately stable:
        /// clients cache the list, and a model's prompt cache only hits if the descriptors arrive the same
        /// way every time - reordering 29 tools would invalidate it for no reason.
        ///
        /// The <c>ttlMs</c>/<c>cacheScope</c> hints go only to a client on the revision that defines them,
        /// for the same reason <c>resultType</c> does: an older client gains nothing from fields it does not
        /// implement, and might validate strictly against a schema without them. Both are safe to offer here
        /// because the registry is built once at start-up - which is also what <c>listChanged: false</c> says.
        /// </summary>
        private JObject BuildToolsListResult(RequestContext context)
        {
            var result = new JObject { ["tools"] = _tools.ToListJson() };
            return context.DeclaresRevisionAtLeast(RequestContext.StatelessRevision)
                ? CacheableResult.ApplyTo(result)
                : result;
        }

        /// <summary>
        /// What this server can do. One builder for <c>initialize</c> and <c>server/discover</c> so the two
        /// answers cannot drift apart - a client is entitled to get the same picture from either.
        /// </summary>
        private JObject BuildCapabilities()
        {
            return new JObject
            {
                ["tools"] = new JObject { ["listChanged"] = false },
                // ServerCapabilities.extensions (2026-07-28 minor change 1). Advertised to every client:
                // one that predates the field ignores it.
                ["extensions"] = new JObject { [TasksExtension] = new JObject() }
            };
        }

        private static JObject ServerIdentity() =>
            new JObject { ["name"] = ServerName, ["version"] = ServerVersion };

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
                return StartTaskCall(tool, name, arguments,
                                     context.DeclaresRevisionAtLeast(RequestContext.StatelessRevision));

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
        private JObject StartTaskCall(McpTool tool, string name, JObject arguments, bool wantsResultType)
        {
            ServerTask task = _taskStore.Create("tools/call " + name);
            task.SetStatusMessage("Queued: " + name + ".");

            // While a task runs there is no open request to attach progress to, so the milestones a tool
            // reports become the task's statusMessage - which is exactly what the client's next poll reads.
            var context = new ToolCallContext((progress, total, message) => task.SetStatusMessage(message));

            _taskRunner.Value.Enqueue(task, () =>
            {
                task.SetStatusMessage("Running " + name + ".");
                // The tool result ends up nested inside a later tasks/get, whose own request context is not
                // this one - so the revision the CALLER asked in decides its shape, decided here (#107).
                return WithResultType(ExecuteTool(tool, name, arguments, context), wantsResultType);
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
