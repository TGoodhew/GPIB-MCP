using System;
using System.Collections.Generic;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Stateless dispatch (#106, SEP-2575): MCP 2026-07-28 drops the handshake, so a request states its own
    /// context in <c>_meta</c> and a result names the server that produced it. The 2025-06-18 handshake keeps
    /// working throughout - every client we ship for still speaks it.
    /// </summary>
    public class StatelessDispatchTests
    {
        private sealed class RecordingSink : IMcpMessageSink
        {
            public readonly List<JObject> Messages = new List<JObject>();
            public void Send(JObject message) { lock (Messages) Messages.Add(message); }
        }

        private static McpTool Echo(string name = "echo") =>
            new McpTool(name, "returns its input", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("ok")));

        private static JObject Request(string method, JObject prms = null, int id = 1)
        {
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (prms != null) message["params"] = prms;
            return message;
        }

        /// <summary>The per-request context a 2026-07-28 client sends instead of shaking hands.</summary>
        private static JObject Meta(string protocolVersion = "2025-06-18", string clientName = "probe",
                                    string logLevel = null, JObject extensions = null)
        {
            var meta = new JObject
            {
                [RequestContext.ProtocolVersionKey] = protocolVersion,
                [RequestContext.ClientInfoKey] = new JObject { ["name"] = clientName, ["version"] = "1.0" }
            };
            if (logLevel != null) meta[RequestContext.LogLevelKey] = logLevel;
            if (extensions != null)
                meta[RequestContext.ClientCapabilitiesKey] = new JObject { ["extensions"] = extensions };
            return meta;
        }

        // ---------------------------------------------------------------- RequestContext

        [Fact]
        public void RequestContext_ReadsEverythingTheRequestDeclares()
        {
            var context = new RequestContext(Meta(logLevel: "debug",
                extensions: new JObject { [McpDispatcher.TasksExtension] = new JObject() }));

            Assert.Equal("2025-06-18", context.ProtocolVersion);
            Assert.Equal("probe", context.ClientName);
            Assert.Equal("debug", context.LogLevel);
            Assert.True(context.DeclaresExtension(McpDispatcher.TasksExtension));
            Assert.False(context.DeclaresExtension("io.example/other"));
            Assert.False(context.IsEmpty);
        }

        [Fact]
        public void RequestContext_IsEmptyAndHarmless_ForAClientThatSendsNoMeta()
        {
            // A 2025-06-18 client sends none of this; nothing may throw and nothing may be inferred.
            foreach (var context in new[] { RequestContext.None, new RequestContext(null), new RequestContext(new JObject()) })
            {
                Assert.True(context.IsEmpty);
                Assert.Null(context.ProtocolVersion);
                Assert.Null(context.ClientName);
                Assert.Null(context.ClientInfo);
                Assert.Null(context.ClientCapabilities);
                Assert.Null(context.LogLevel);
                Assert.Null(context.ProgressToken);
                Assert.False(context.DeclaresExtension(McpDispatcher.TasksExtension));
            }
        }

        [Fact]
        public void RequestContext_TreatsAnExplicitNullProgressTokenAsAbsent()
        {
            var context = new RequestContext(new JObject { ["progressToken"] = JValue.CreateNull() });
            Assert.Null(context.ProgressToken);
        }

        // ---------------------------------------------------------------- serverInfo on results

        [Fact]
        public void EveryResult_NamesTheServerInItsMeta()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                var requests = new[]
                {
                    Request("initialize", new JObject { ["capabilities"] = new JObject() }),
                    Request("ping"),
                    Request("tools/list"),
                    Request("tools/call", new JObject { ["name"] = "echo", ["arguments"] = new JObject() })
                };

                foreach (JObject request in requests)
                {
                    JToken serverInfo = dispatcher.Dispatch(request)["result"]["_meta"][RequestContext.ServerInfoKey];
                    Assert.Equal(McpDispatcher.ServerName, (string)serverInfo["name"]);
                    Assert.Equal(McpDispatcher.ServerVersion, (string)serverInfo["version"]);
                }
            }
        }

        [Fact]
        public void ServerInfo_DoesNotDisturbTheResultItRidesOn()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject result = (JObject)dispatcher.Dispatch(Request("tools/call",
                    new JObject { ["name"] = "echo", ["arguments"] = new JObject() }))["result"];

                Assert.Equal("ok", (string)result["content"][0]["text"]);
                Assert.NotNull(result["_meta"]);
            }
        }

        [Fact]
        public void ErrorsCarryNoServerInfo_BecauseTheyCarryNoResult()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(Request("no/such/method"));
                Assert.Null(response["result"]);
                Assert.Equal(-32601, (int)response["error"]["code"]);
            }
        }

        // ---------------------------------------------------------------- legacy paths stay

        [Fact]
        public void TheHandshakeKeepsWorking_ForClientsStillOnTheOlderRevision()
        {
            // 2026-07-28 removes initialize/notifications/initialized/ping. They stay for the whole
            // deprecation window: Claude Desktop, the .mcpb bundle and the HTTP connectors all use them.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject init = (JObject)dispatcher.Dispatch(Request("initialize",
                    new JObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JObject() }))["result"];
                Assert.Equal("2025-06-18", (string)init["protocolVersion"]);

                Assert.NotNull(dispatcher.Dispatch(Request("ping"))["result"]);

                // A notification returns nothing and must not throw.
                Assert.Null(dispatcher.Dispatch(new JObject
                    { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" }));
            }
        }

        [Fact]
        public void APerRequestContext_IsEnoughToUseTheServer_WithNoHandshakeAtAll()
        {
            // The stateless shape: no initialize, every request self-describing.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject result = (JObject)dispatcher.Dispatch(Request("tools/call", new JObject
                {
                    ["name"] = "echo",
                    ["arguments"] = new JObject(),
                    ["_meta"] = Meta(logLevel: "info")
                }))["result"];

                Assert.Equal("ok", (string)result["content"][0]["text"]);
            }
        }

        [Fact]
        public void AnUnsupportedPerRequestProtocolVersion_IsAnsweredRatherThanRefused()
        {
            // #104 governs what we claim; here the point is only that an unknown revision does not break
            // dispatch. Refusing it outright waits on the error-code allocation policy (#109).
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(Request("tools/list",
                    new JObject { ["_meta"] = Meta(protocolVersion: "2026-07-28") }));

                Assert.Null(response["error"]);
                Assert.NotNull(response["result"]["tools"]);
            }
        }

        // ---------------------------------------------------------------- logging deprecation

        [Fact]
        public void NoLogNotificationIsEverEmitted_EvenWhenARequestAsksForALevel()
        {
            // Servers MUST NOT emit notifications/message for a request that omitted logLevel - and we emit
            // none regardless: diagnostics go to stderr, which is the migration for deprecated Logging.
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                dispatcher.Notifications = sink;
                dispatcher.Dispatch(Request("tools/call", new JObject
                {
                    ["name"] = "echo",
                    ["arguments"] = new JObject(),
                    ["_meta"] = Meta(logLevel: "debug")
                }));
                dispatcher.Dispatch(Request("tools/list"));
            }

            Assert.DoesNotContain(sink.Messages, m => (string)m["method"] == "notifications/message");
        }

        [Fact]
        public void LoggingSetLevel_IsNotServed()
        {
            // Removed in 2026-07-28, and we never implemented it - the level is a per-request hint now.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(Request("logging/setLevel",
                    new JObject { ["level"] = "debug" }));
                Assert.Equal(-32601, (int)response["error"]["code"]);
            }
        }
    }
}
