using System;
using System.Collections.Generic;
using System.Linq;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// <c>subscriptions/listen</c> (#111): the long-lived change-notification stream that replaced the HTTP
    /// GET endpoint. This server has nothing to subscribe to - the tool list is fixed at start-up and there
    /// are no resources or prompts - so the conformant answer is to acknowledge, agree to nothing, and close
    /// the subscription cleanly rather than leave a stream open promising messages that can never come.
    /// </summary>
    public class SubscriptionsTests
    {
        private sealed class RecordingSink : IMcpMessageSink
        {
            public readonly List<JObject> Messages = new List<JObject>();
            public void Send(JObject message) { lock (Messages) Messages.Add(message); }
        }

        private static McpTool Echo() =>
            new McpTool("echo", "returns its input", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("ok")));

        private static JObject Listen(object id = null) => new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id == null ? 1 : JToken.FromObject(id),
            ["method"] = "subscriptions/listen",
            ["params"] = new JObject
            {
                ["notifications"] = new JObject
                {
                    ["toolsListChanged"] = true,
                    ["resourcesListChanged"] = true,
                    ["resourceSubscriptions"] = new JArray("file:///nope")
                }
            }
        };

        [Fact]
        public void Listen_IsAnswered_NotRefusedAsAnUnknownMethod()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(Listen());
                Assert.Null(response["error"]);
                Assert.NotNull(response["result"]);
            }
        }

        [Fact]
        public void Listen_AcknowledgesFirst_AgreeingToNothing()
        {
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                dispatcher.Notifications = sink;
                dispatcher.Dispatch(Listen(7));

                JObject ack = sink.Messages.Single();
                Assert.Equal("notifications/subscriptions/acknowledged", (string)ack["method"]);

                // The acknowledged filter is the subset the server agreed to honour. We support none of the
                // types, so it is empty - and a client reading it learns exactly that.
                var agreed = (JObject)ack["params"]["notifications"];
                Assert.Empty(agreed.Properties());

                Assert.Equal(7, (int)ack["params"]["_meta"][RequestContext.SubscriptionIdKey]);
            }
        }

        [Fact]
        public void Listen_ClosesTheSubscriptionGracefully()
        {
            // The empty result IS the graceful closure: it tells the client the subscription ended cleanly,
            // as opposed to a transport that just dropped.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject result = (JObject)dispatcher.Dispatch(Listen(7))["result"];

                Assert.Equal(7, (int)result["_meta"][RequestContext.SubscriptionIdKey]);
                Assert.Null(result["notifications"]);
                // The server identity every result carries rides alongside it (#106).
                Assert.NotNull(result["_meta"][RequestContext.ServerInfoKey]);
            }
        }

        [Fact]
        public void TheAcknowledgementComesBeforeTheResponse()
        {
            // MUST be the first message on the subscription. Over stdio both travel the same channel, so the
            // ordering is observable - and wrong ordering would have the client see a closed subscription
            // before it knew one existed.
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                dispatcher.Notifications = sink;
                Assert.Empty(sink.Messages);

                dispatcher.Dispatch(Listen());
                Assert.Single(sink.Messages);   // sent during dispatch, i.e. before the response was returned
            }
        }

        [Fact]
        public void ListenWorksWithNoOutboundChannel()
        {
            // The HTTP transport has none: a POST gets one JSON response. The caller still gets the closure,
            // and with no notification types agreed the two messages say the same thing.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(Listen(3));
                Assert.Null(response["error"]);
                Assert.Equal(3, (int)response["result"]["_meta"][RequestContext.SubscriptionIdKey]);
            }
        }

        [Fact]
        public void TheSubscriptionIdIsTheRequestId_WhateverItsType()
        {
            // JSON-RPC ids may be strings; the subscription id is that id, not a number we invent.
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                dispatcher.Notifications = sink;
                JObject response = dispatcher.Dispatch(Listen("sub-a"));

                Assert.Equal("sub-a", (string)response["result"]["_meta"][RequestContext.SubscriptionIdKey]);
                Assert.Equal("sub-a", (string)sink.Messages.Single()["params"]["_meta"][RequestContext.SubscriptionIdKey]);
            }
        }

        [Fact]
        public void NoChangeNotificationIsEverSentUnasked()
        {
            // The server MUST NOT send notification types the client did not request. We send none at all -
            // there is nothing here that changes - and this pins that as the tool list grows.
            var sink = new RecordingSink();
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                dispatcher.Notifications = sink;
                dispatcher.Dispatch(new JObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list" });
                dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call",
                    ["params"] = new JObject { ["name"] = "echo", ["arguments"] = new JObject() }
                });

                Assert.DoesNotContain(sink.Messages,
                    m => ((string)m["method"] ?? "").StartsWith("notifications/", StringComparison.Ordinal));
            }
        }
    }
}
