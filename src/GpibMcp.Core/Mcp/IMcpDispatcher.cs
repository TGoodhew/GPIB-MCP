using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The transport-agnostic core of the MCP server: turns one parsed JSON-RPC message into the response
    /// to send back. Knows nothing about how messages arrive or leave (stdio, HTTP, …) - that is a
    /// <see cref="IMcpTransport"/>'s job. Implementations are safe to call from one request at a time;
    /// <see cref="McpDispatcher"/> serializes internally so any transport, including a concurrent one, is safe.
    /// </summary>
    public interface IMcpDispatcher
    {
        /// <summary>
        /// Processes one JSON-RPC message and returns the response object to send back, or <c>null</c> when
        /// there is nothing to send (a notification, or a message with no <c>method</c>).
        /// </summary>
        JObject Dispatch(JObject message);

        /// <summary>
        /// The channel for server→client messages that are not a response - progress while a slow tool runs
        /// (#112). A transport that can carry them sets this before pumping messages; one that cannot leaves
        /// it <c>null</c> and the dispatcher simply does not report progress.
        /// </summary>
        IMcpMessageSink Notifications { get; set; }
    }
}
