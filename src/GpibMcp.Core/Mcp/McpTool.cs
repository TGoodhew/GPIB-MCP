using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// A single callable MCP tool: its advertised schema plus the handler that runs it.
    /// The handler receives the parsed "arguments" object and returns human/agent-readable text.
    /// Throwing from a handler is reported back to the client as an (isError) tool result.
    /// </summary>
    public sealed class McpTool
    {
        public string Name { get; }
        public string Description { get; }
        public JObject InputSchema { get; }

        /// <summary>
        /// Marks a tool whose calls routinely take seconds rather than milliseconds - a screen capture, a
        /// batch sweep. Two things key off it (#112): the dispatcher may run the call as a task instead of
        /// blocking, and the handler is given a <see cref="ToolCallContext"/> worth reporting progress to.
        /// Fast tools stay exactly as they were.
        /// </summary>
        public bool LongRunning { get; }

        /// <summary>
        /// Optional JSON Schema for the tool's <c>structuredContent</c> (#113). Declaring one is a promise:
        /// a successful call returns a payload that conforms, so the model reads named fields instead of
        /// re-parsing prose. Tools that only ever return prose leave it null and nothing changes for them.
        /// </summary>
        public JObject OutputSchema { get; private set; }

        /// <summary>Declares the result schema. Returns this, so it chains onto the constructor call.</summary>
        public McpTool WithOutputSchema(JObject outputSchema)
        {
            OutputSchema = outputSchema;
            return this;
        }

        private readonly Func<JObject, ToolCallContext, ToolOutput> _handler;

        /// <summary>Text-returning tool: the string becomes a single text content block.</summary>
        public McpTool(string name, string description, JObject inputSchema, Func<JObject, string> handler)
            : this(name, description, inputSchema, Wrap(handler), false)
        {
        }

        /// <summary>Rich tool: returns one or more content blocks (text and/or images).</summary>
        public McpTool(string name, string description, JObject inputSchema, Func<JObject, ToolOutput> handler)
            : this(name, description, inputSchema, Ignore(handler), false)
        {
        }

        /// <summary>
        /// Slow tool: additionally receives the call context, so it can report progress as it goes and see a
        /// cancellation request. Pass <paramref name="longRunning"/> as false for a context-aware tool that
        /// should still always run synchronously.
        /// </summary>
        public McpTool(string name, string description, JObject inputSchema,
                       Func<JObject, ToolCallContext, ToolOutput> handler, bool longRunning = true)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema ?? new JObject { ["type"] = "object" };
            LongRunning = longRunning;
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        private static Func<JObject, ToolCallContext, ToolOutput> Wrap(Func<JObject, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return (args, ctx) => ToolOutput.Text(handler(args));
        }

        private static Func<JObject, ToolCallContext, ToolOutput> Ignore(Func<JObject, ToolOutput> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return (args, ctx) => handler(args);
        }

        public ToolOutput Invoke(JObject arguments) => Invoke(arguments, ToolCallContext.None);

        public ToolOutput Invoke(JObject arguments, ToolCallContext context) =>
            _handler(arguments ?? new JObject(), context ?? ToolCallContext.None);

        /// <summary>Serializes this tool into the shape expected by tools/list.</summary>
        public JObject ToDescriptor()
        {
            var descriptor = new JObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["inputSchema"] = InputSchema
            };
            if (OutputSchema != null) descriptor["outputSchema"] = OutputSchema;
            return descriptor;
        }
    }

    /// <summary>Ordered, name-indexed collection of the tools the server exposes.</summary>
    public sealed class ToolRegistry
    {
        private readonly List<McpTool> _ordered = new List<McpTool>();
        private readonly Dictionary<string, McpTool> _byName =
            new Dictionary<string, McpTool>(StringComparer.Ordinal);

        public ToolRegistry Add(McpTool tool)
        {
            if (_byName.ContainsKey(tool.Name))
                throw new InvalidOperationException("Duplicate tool name: " + tool.Name);
            _ordered.Add(tool);
            _byName[tool.Name] = tool;
            return this;
        }

        public bool TryGet(string name, out McpTool tool) => _byName.TryGetValue(name ?? "", out tool);

        /// <summary>The registered tools, in registration order (for self-description / overviews).</summary>
        public IReadOnlyList<McpTool> Tools => _ordered;

        /// <summary>Number of registered tools.</summary>
        public int Count => _ordered.Count;

        public JArray ToListJson()
        {
            var arr = new JArray();
            foreach (var t in _ordered) arr.Add(t.ToDescriptor());
            return arr;
        }
    }

    /// <summary>
    /// A JSON-RPC error to surface to the client (maps to the "error" member).
    ///
    /// <b>Code allocation (MCP 2026-07-28, minor change 12).</b> The JSON-RPC server-error range is split:
    /// <c>-32000</c>…<c>-32019</c> is implementation-defined and ours to use, <c>-32020</c>…<c>-32099</c> is
    /// reserved for the specification. Any code this server invents must sit in the first block - taking one
    /// from the reserved half would collide with a future spec code and mean something else to every client.
    /// <see cref="IsImplementationDefined"/> exists so a test can hold us to that.
    ///
    /// Everything we emit today is a standard JSON-RPC code (<c>-32601</c>, <c>-32602</c>, <c>-32603</c>,
    /// <c>-32700</c>); the spec-allocated codes below are named so that when we do need one we use the
    /// number the specification chose rather than inventing a parallel meaning.
    /// </summary>
    public sealed class McpError : Exception
    {
        /// <summary>First code in the block implementations may allocate from.</summary>
        public const int ImplementationDefinedFirst = -32019;

        /// <summary>Last code in the block implementations may allocate from.</summary>
        public const int ImplementationDefinedLast = -32000;

        /// <summary>A header required by the transport disagreed with the request body (renumbered from -32001).</summary>
        public const int HeaderMismatchCode = -32020;

        /// <summary>The request needed a client capability the client did not declare (renumbered from -32003).</summary>
        public const int MissingRequiredClientCapabilityCode = -32021;

        /// <summary>The client asked for a protocol revision this server does not implement (renumbered from -32004).</summary>
        public const int UnsupportedProtocolVersionCode = -32022;

        public int Code { get; }
        public JToken ErrorData { get; }

        public McpError(int code, string message, JToken data = null) : base(message)
        {
            Code = code;
            ErrorData = data;
        }

        /// <summary>True when <paramref name="code"/> is one an implementation may allocate for itself.</summary>
        public static bool IsImplementationDefined(int code) =>
            code >= ImplementationDefinedFirst && code <= ImplementationDefinedLast;

        // Standard JSON-RPC codes used by this server.
        public static McpError MethodNotFound(string method) =>
            new McpError(-32601, "Method not found: " + method);

        public static McpError InvalidParams(string message) =>
            new McpError(-32602, "Invalid params: " + message);

        // Spec-allocated MCP codes.

        /// <summary>
        /// The revision the request asked for is not one this server speaks. The data carries the list that
        /// is, so a client can pick one and retry rather than guess a second time.
        /// </summary>
        public static McpError UnsupportedProtocolVersion(string requested, IEnumerable<string> supported)
        {
            var versions = new JArray();
            if (supported != null) foreach (string v in supported) versions.Add(v);
            return new McpError(UnsupportedProtocolVersionCode,
                "Unsupported protocol version: " + (requested ?? "(none)"),
                new JObject { ["requested"] = requested, ["supported"] = versions });
        }

        /// <summary>The request cannot be served because the client never declared <paramref name="capability"/>.</summary>
        public static McpError MissingRequiredClientCapability(string capability) =>
            new McpError(MissingRequiredClientCapabilityCode,
                "Missing required client capability: " + capability,
                new JObject { ["capability"] = capability });

        /// <summary>A transport header contradicted the request it was carrying.</summary>
        public static McpError HeaderMismatch(string header, string expected, string actual) =>
            new McpError(HeaderMismatchCode,
                "Header '" + header + "' does not match the request" +
                (expected == null ? "" : " (expected '" + expected + "', got '" + (actual ?? "(none)") + "')"),
                new JObject { ["header"] = header, ["expected"] = expected, ["actual"] = actual });
    }
}
