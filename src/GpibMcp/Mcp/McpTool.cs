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
        private readonly Func<JObject, ToolOutput> _handler;

        /// <summary>Text-returning tool: the string becomes a single text content block.</summary>
        public McpTool(string name, string description, JObject inputSchema, Func<JObject, string> handler)
            : this(name, description, inputSchema, Wrap(handler))
        {
        }

        /// <summary>Rich tool: returns one or more content blocks (text and/or images).</summary>
        public McpTool(string name, string description, JObject inputSchema, Func<JObject, ToolOutput> handler)
        {
            Name = name;
            Description = description;
            InputSchema = inputSchema ?? new JObject { ["type"] = "object" };
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        private static Func<JObject, ToolOutput> Wrap(Func<JObject, string> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return args => ToolOutput.Text(handler(args));
        }

        public ToolOutput Invoke(JObject arguments) => _handler(arguments ?? new JObject());

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
