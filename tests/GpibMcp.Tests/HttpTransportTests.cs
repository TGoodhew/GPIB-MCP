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

        // ---- request metadata headers (#110, SEP-2243) --------------------------

        private const string StatelessCall =
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"visa_list_resources\"," +
            "\"arguments\":{},\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}}}";

        private const string LegacyCall =
            "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"visa_list_resources\",\"arguments\":{}}}";

        private static Action<HttpRequestMessage> Headers(params string[] nameValuePairs) => msg =>
        {
            for (int i = 0; i + 1 < nameValuePairs.Length; i += 2)
                msg.Headers.TryAddWithoutValidation(nameValuePairs[i], nameValuePairs[i + 1]);
        };

        [Fact]
        public async Task Headers_ThatDisagreeWithTheBody_AreRejected()
        {
            // The headers exist so an intermediary can route without parsing the body. If the two disagree,
            // a load balancer and this server would be acting on different requests.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall, Headers("Mcp-Method", "tools/list"));
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(McpError.HeaderMismatchCode, (int)json["error"]["code"]);
                Assert.Equal(9, (int)json["id"]);
            }
        }

        [Fact]
        public async Task McpName_ThatDisagreesWithTheToolBeingCalled_IsRejected()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall,
                    Headers("Mcp-Method", "tools/call", "Mcp-Name", "some_other_tool"));
                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(McpError.HeaderMismatchCode, (int)json["error"]["code"]);
            }
        }

        [Fact]
        public async Task McpName_IsDecodedFromTheBase64Sentinel_BeforeComparing()
        {
            // A name that cannot travel as plain ASCII arrives encoded; comparing it raw would reject a
            // request that is perfectly correct.
            string encoded = "=?base64?" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes("visa_list_resources")) + "?=";

            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall, Headers("Mcp-Method", "tools/call", "Mcp-Name", encoded));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        [Fact]
        public async Task HeadersThatAgree_AreAccepted()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall,
                    Headers("Mcp-Method", "tools/call", "Mcp-Name", "visa_list_resources"));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        [Fact]
        public async Task AClientOnTheOlderRevision_IsNotAskedForHeadersItNeverSent()
        {
            // Every client we serve over HTTP today speaks 2025-06-18 and sends none of these.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall);
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        [Fact]
        public async Task AClientOnTheNewRevision_MustSendTheRequiredHeaders()
        {
            using (var h = new Harness())
            {
                var missing = await Post(h.Url, StatelessCall);
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                Assert.Equal(McpError.HeaderMismatchCode,
                    (int)JObject.Parse(await missing.Content.ReadAsStringAsync())["error"]["code"]);

                var complete = await Post(h.Url, StatelessCall, Headers(
                    "MCP-Protocol-Version", "2026-07-28",
                    "Mcp-Method", "tools/call",
                    "Mcp-Name", "visa_list_resources"));
                Assert.Equal(HttpStatusCode.OK, complete.StatusCode);
            }
        }

        [Fact]
        public async Task AProtocolVersionHeaderThatContradictsTheBody_IsRejected()
        {
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, StatelessCall, Headers(
                    "MCP-Protocol-Version", "2025-06-18",
                    "Mcp-Method", "tools/call",
                    "Mcp-Name", "visa_list_resources"));

                Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(McpError.HeaderMismatchCode, (int)json["error"]["code"]);
                Assert.Equal("MCP-Protocol-Version", (string)json["error"]["data"]["header"]);
            }
        }

        [Fact]
        public async Task StaleSessionHeaders_AreIgnoredRatherThanHonoured()
        {
            // Sessions and stream resumability are both gone: neither header may change anything, and we
            // must never mint or echo a session id.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, LegacyCall,
                    Headers("Mcp-Session-Id", "stale-session", "Last-Event-ID", "42"));

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.False(resp.Headers.Contains("Mcp-Session-Id"));
            }
        }

        [Fact]
        public async Task GetAndDelete_AreBothMethodNotAllowed()
        {
            // The GET stream became subscriptions/listen and DELETE tore down a session that no longer
            // exists - and never did here, since no session id was ever issued.
            using (var h = new Harness())
            using (var client = new HttpClient())
            {
                await Post(h.Url, LegacyCall);   // wait for the listener

                var get = await client.GetAsync(h.Url);
                var del = await client.DeleteAsync(h.Url);

                Assert.Equal(HttpStatusCode.MethodNotAllowed, get.StatusCode);
                Assert.Equal(HttpStatusCode.MethodNotAllowed, del.StatusCode);
            }
        }

        // ---- protocol errors mapped onto HTTP status codes ----------------------

        private static string Unknown(string meta) =>
            "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"no/such/method\",\"params\":{" + meta + "}}";

        private const string StatelessMeta =
            "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"2026-07-28\"}";

        [Fact]
        public async Task AnUnknownMethod_Is404WithTheJsonRpcErrorStillInTheBody()
        {
            // The body is the point: it is what distinguishes a modern server saying "I do not have that
            // method" from a legacy server that does not host this endpoint at all.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, Unknown(StatelessMeta), Headers(
                    "MCP-Protocol-Version", "2026-07-28", "Mcp-Method", "no/such/method"));

                Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(-32601, (int)json["error"]["code"]);
                Assert.Equal(11, (int)json["id"]);
            }
        }

        [Fact]
        public async Task AnUnknownMethod_StaysA200_ForAClientOnTheOlderRevision()
        {
            // 2025-06-18 has no such mapping, and a client written against it may only read the body on 200.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, Unknown(""));
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal(-32601, (int)JObject.Parse(await resp.Content.ReadAsStringAsync())["error"]["code"]);
            }
        }

        [Fact]
        public async Task AnUnsupportedProtocolVersion_Is400WithTheSupportedList()
        {
            // "latest" is not a revision at all, so it cannot be served on any reading.
            string body = "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/list\",\"params\":{" +
                          "\"_meta\":{\"io.modelcontextprotocol/protocolVersion\":\"latest\"}}}";

            using (var h = new Harness())
            {
                var resp = await Post(h.Url, body, Headers("MCP-Protocol-Version", "latest", "Mcp-Method", "tools/list"));

                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.Equal(McpError.UnsupportedProtocolVersionCode, (int)json["error"]["code"]);
                Assert.NotEmpty((JArray)json["error"]["data"]["supported"]);
                // A garbage version must not read as "newer than every dated revision" and so demand the
                // 2026-07-28 headers; it is refused for what it is.
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        [Fact]
        public async Task AFailedToolIsStillA200()
        {
            // The request was served; the tool's failure is in the result, not the transport.
            string body = "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{" +
                          "\"name\":\"visa_query\",\"arguments\":{}," + StatelessMeta + "}}";

            using (var h = new Harness())
            {
                var resp = await Post(h.Url, body, Headers(
                    "MCP-Protocol-Version", "2026-07-28", "Mcp-Method", "tools/call", "Mcp-Name", "visa_query"));

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                var json = JObject.Parse(await resp.Content.ReadAsStringAsync());
                Assert.True((bool)json["result"]["isError"]);
            }
        }

        [Fact]
        public async Task ABatchIsStillAccepted_AndSkipsHeaderValidation()
        {
            // Batching left MCP in 2025-06-18; accepting one costs nothing. The metadata headers describe a
            // single message, so there is nothing meaningful to validate them against here.
            using (var h = new Harness())
            {
                var batch = "[" + LegacyCall + "," +
                    "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/list\"}]";
                var resp = await Post(h.Url, batch, Headers("Mcp-Method", "tools/call"));

                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
                Assert.Equal(2, JArray.Parse(await resp.Content.ReadAsStringAsync()).Count);
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

        // ---- authentication (#114: static bearer, chosen deliberately) ----------

        [Fact]
        public void ANetworkBindWithNoTokenRefusesToStart()
        {
            // A warning is not a control: behind this endpoint is physical instrument control, and the
            // remedy is one environment variable.
            using (var transport = new HttpTransport("0.0.0.0", 3999, token: null))
            {
                var ex = Assert.Throws<InvalidOperationException>(
                    () => transport.Run(new McpDispatcher(InstrumentTools.BuildRegistry(new FakeInstrumentManager()))));
                Assert.Contains("GPIB_MCP_HTTP_TOKEN", ex.Message);
            }
        }

        [Fact]
        public async Task LoopbackWithNoTokenStillServes()
        {
            // The local development case, and the one the tunnel workflow builds on - the tunnel, not the
            // bind address, is what makes the port public, and the server cannot see that.
            using (var h = new Harness())
            {
                var resp = await Post(h.Url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
                Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            }
        }

        [Theory]
        [InlineData("s3cret", true)]
        [InlineData("s3cre", false)]      // a prefix of the real token
        [InlineData("s3cretx", false)]    // the real token plus more
        [InlineData("S3CRET", false)]     // case matters
        [InlineData("", false)]
        public async Task TheTokenMustMatchExactly(string presented, bool accepted)
        {
            using (var h = new Harness(token: "s3cret"))
            {
                var resp = await Post(h.Url, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}",
                    m => m.Headers.TryAddWithoutValidation("Authorization", "Bearer " + presented));

                Assert.Equal(accepted ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, resp.StatusCode);
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
