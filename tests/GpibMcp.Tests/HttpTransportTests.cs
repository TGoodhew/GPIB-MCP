using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GpibMcp.Http;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #68: integration tests for the Streamable HTTP transport - real HTTP requests against a localhost
    /// HttpListener, exercising the /mcp endpoint, batch, notifications (202), auth, and the security guards.
    /// </summary>
    public class HttpTransportTests
    {
        static HttpTransportTests()
        {
            Environment.SetEnvironmentVariable("GPIB_MCP_TOOL_CALL_LOG",
                Path.Combine(Path.GetTempPath(), "gpibmcp-test-tool-calls.log"));
        }

        /// <summary>A running transport on a free loopback port; dispose to stop.</summary>
        private sealed class Harness : IDisposable
        {
            public string Url { get; }
            private readonly HttpTransport _transport;

            public Harness(string token = null)
            {
                int port = FreePort();
                Url = "http://127.0.0.1:" + port + "/mcp";
                var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
                var dispatcher = new McpDispatcher(registry);
                _transport = new HttpTransport("127.0.0.1", port, token);
                var t = new Thread(() => _transport.Run(dispatcher)) { IsBackground = true };
                t.Start();
            }

            public void Dispose() => _transport.Dispose();

            private static int FreePort()
            {
                var l = new TcpListener(IPAddress.Loopback, 0);
                l.Start();
                int p = ((IPEndPoint)l.LocalEndpoint).Port;
                l.Stop();
                return p;
            }
        }

        private static readonly string Init =
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"t\",\"version\":\"1\"}}}";

        // POST with a brief retry so the test doesn't race the listener's startup.
        private static async Task<HttpResponseMessage> Post(string url, string body, Action<HttpRequestMessage> tweak = null)
        {
            using (var client = new HttpClient())
            {
                for (int attempt = 0; ; attempt++)
                {
                    var msg = new HttpRequestMessage(HttpMethod.Post, url)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    tweak?.Invoke(msg);
                    try { return await client.SendAsync(msg); }
                    catch (HttpRequestException) when (attempt < 40) { await Task.Delay(50); }
                }
            }
        }

        [Fact]
        public async Task Post_Initialize_ReturnsServerInfo()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, Init);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(McpDispatcher.ServerName, (string)json["result"]["serverInfo"]["name"]);
            }
        }

        [Fact]
        public async Task Post_ToolsCall_RunsTheTool()
        {
            using (var h = new Harness())
            {
                var call = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"visa_list_resources\",\"arguments\":{}}}";
                var resp = await Post(h.Url, call);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.NotNull(json["result"]["content"]);
            }
        }

        [Fact]
        public async Task Post_Notification_Returns202_NoBody()
        {
            using (var h = new Harness())
            {
                var note = "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";
                var resp = await Post(h.Url, note);
                Assert.Equal(HttpStatusCode.Accepted, resp.StatusCode);
                Assert.Equal(string.Empty, await resp.Content.ReadAsStringAsync());
            }
        }

        [Fact]
        public async Task Post_Batch_ReturnsArrayOfResponses()
        {
            using (var h = new Harness())
            {
                var batch = "[" + Init + ",{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"ping\"}]";
                var resp = await Post(h.Url, batch);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var arr = JArray.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(2, arr.Count);
            }
        }

        [Fact]
        public async Task Get_EventStream_NotSupported_405()
        {
            using (var h = new Harness())
            {
                // wait for readiness via a POST first
                await Post(h.Url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                using (var client = new HttpClient())
                {
                    var resp = await client.GetAsync(h.Url);
                    Assert.Equal(HttpStatusCode.MethodNotAllowed, resp.StatusCode);
                }
            }
        }

        [Fact]
        public async Task UnknownPath_Returns404()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url.Replace("/mcp", "/nope"), "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
            }
        }

        [Fact]
        public async Task ForbiddenOrigin_Returns403()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}",
                    m => m.Headers.Add("Origin", "http://evil.example.com"));
                Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
            }
        }

        [Fact]
        public async Task BearerToken_RequiredWhenConfigured()
        {
            using (var h = new Harness(token: "s3cret"))
            {
                // No Authorization -> 401
                var noAuth = await Post(h.Url, Init);
                Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

                // Correct token -> 200
                var ok = await Post(h.Url, Init, m => m.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "s3cret"));
                Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
            }
        }
    }
}
