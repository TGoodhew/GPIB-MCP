using Newtonsoft.Json.Linq;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The channel a transport offers for server→client messages that are not the response to a request -
    /// today that means <c>notifications/progress</c> emitted while a slow tool call is still running (#112).
    ///
    /// Not every transport can carry one. Stdio can: the output stream is always open, so a notification is
    /// just another line. Our Streamable HTTP transport cannot: a POST gets exactly one JSON response and we
    /// offer no server→client stream, so it simply leaves the sink unattached and the dispatcher drops
    /// progress. (Reinstating it there is <c>subscriptions/listen</c> - issue #111.)
    /// </summary>
    public interface IMcpMessageSink
    {
        /// <summary>Sends one JSON-RPC message to the client. Must be safe to call from any thread.</summary>
        void Send(JObject message);
    }
}
