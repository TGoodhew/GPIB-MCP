using System;
using System.Linq;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// <c>server/discover</c> (#105): the versions we speak, what we can do, and who we are - in one request
    /// and with no handshake first. MCP 2026-07-28 makes it a MUST, and on stdio it is also how a client
    /// tells a modern server from a legacy one.
    /// </summary>
    public class ServerDiscoverTests
    {
        private static McpTool Echo() =>
            new McpTool("echo", "returns its input", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("ok")));

        private static JObject Discover(McpDispatcher dispatcher, JObject meta = null)
        {
            var prms = new JObject();
            if (meta != null) prms["_meta"] = meta;
            return (JObject)dispatcher.Dispatch(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = "discover-1", ["method"] = "server/discover", ["params"] = prms
            })["result"];
        }

        [Fact]
        public void Discover_ReportsTheVersionsWeActuallySpeak()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                var versions = ((JArray)Discover(dispatcher)["supportedVersions"]).Select(v => (string)v).ToArray();

                Assert.Equal(McpDispatcher.SupportedProtocolVersions, versions);
                Assert.Equal(RequestContext.StatelessRevision, versions.First());   // newest first

                // The list is what we implement, not what we have heard of: 2025-11-25 is a real revision
                // whose changes have not been reviewed here, so it stays off (#104's rule, still holding).
                Assert.DoesNotContain("2025-11-25", versions);
            }
        }

        [Fact]
        public void Discover_ReportsCapabilitiesAndIdentityAndInstructions()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo()), "CAPABILITY SUMMARY"))
            {
                JObject result = Discover(dispatcher);

                Assert.NotNull(result["capabilities"]["tools"]);
                Assert.NotNull(result["capabilities"]["extensions"][McpDispatcher.TasksExtension]);
                Assert.Equal("CAPABILITY SUMMARY", (string)result["instructions"]);

                // Identity rides in _meta, where every result carries it (#106).
                JToken serverInfo = result["_meta"][RequestContext.ServerInfoKey];
                Assert.Equal(McpDispatcher.ServerName, (string)serverInfo["name"]);
                Assert.Equal(McpDispatcher.ServerVersion, (string)serverInfo["version"]);
            }
        }

        [Fact]
        public void Discover_AndInitialize_CannotDriftApart()
        {
            // One capability builder feeds both; a client is entitled to the same picture from either.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject initialize = (JObject)dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize",
                    ["params"] = new JObject { ["capabilities"] = new JObject() }
                })["result"];

                Assert.True(JToken.DeepEquals(initialize["capabilities"], Discover(dispatcher)["capabilities"]));
            }
        }

        [Fact]
        public void Discover_IsCacheable()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject result = Discover(dispatcher);
                Assert.Equal(CacheableResult.DefaultTtlMs, (int)result["ttlMs"]);
                Assert.Equal("private", (string)result["cacheScope"]);
            }
        }

        [Fact]
        public void Discover_NeedsNoHandshakeAndNoParams()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "server/discover"
                });

                Assert.Null(response["error"]);
                Assert.NotNull(response["result"]["supportedVersions"]);
            }
        }

        [Fact]
        public void Discover_CarriesResultType_ForACallerOnTheNewRevision()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                var meta = new JObject
                {
                    [RequestContext.ProtocolVersionKey] = RequestContext.StatelessRevision,
                    [RequestContext.ClientInfoKey] = new JObject { ["name"] = "probe", ["version"] = "1" }
                };
                Assert.Equal("complete", (string)Discover(dispatcher, meta)["resultType"]);
            }
        }

        [Fact]
        public void UnknownMethods_StillGetACleanMethodNotFound()
        {
            // -32601 is the documented signal a client uses to fall back to the legacy handshake, so the
            // stdio path must answer it rather than die.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 3, ["method"] = "server/nonsense"
                });

                Assert.Equal(-32601, (int)response["error"]["code"]);
                Assert.Contains("server/nonsense", (string)response["error"]["message"]);
            }
        }
    }
}
