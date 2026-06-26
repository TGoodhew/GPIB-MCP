using System;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// A wire transport for the MCP server: receives JSON-RPC messages, hands each to an
    /// <see cref="IMcpDispatcher"/>, and delivers the responses. Each transport (stdio, HTTP, …) is its own
    /// module that depends only on this interface and <see cref="IMcpDispatcher"/> - never the other way
    /// round - so the core stays free of any transport detail.
    /// </summary>
    public interface IMcpTransport : IDisposable
    {
        /// <summary>
        /// Runs the transport, pumping received messages through <paramref name="dispatcher"/> and sending
        /// the responses, until the transport ends (client disconnects / the server is shut down). Blocking.
        /// </summary>
        void Run(IMcpDispatcher dispatcher);
    }
}
