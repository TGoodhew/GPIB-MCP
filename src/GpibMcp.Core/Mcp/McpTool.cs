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
            return new JObject
            {
                ["name"] = Name,
                ["description"] = Description,
                ["inputSchema"] = InputSchema
            };
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

    /// <summary>A JSON-RPC error to surface to the client (maps to the "error" member).</summary>
    public sealed class McpError : Exception
    {
        public int Code { get; }
        public JToken ErrorData { get; }

        public McpError(int code, string message, JToken data = null) : base(message)
        {
            Code = code;
            ErrorData = data;
        }

        // Standard JSON-RPC codes used by this server.
        public static McpError MethodNotFound(string method) =>
            new McpError(-32601, "Method not found: " + method);

        public static McpError InvalidParams(string message) =>
            new McpError(-32602, "Invalid params: " + message);
    }
}
