using System;
using System.Linq;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// The JSON-RPC error-code allocation policy (#109): -32000..-32019 is ours to allocate from,
    /// -32020..-32099 belongs to the specification. Taking a code from the reserved half would collide with
    /// a future spec code and mean something else to every client, so the split is worth a test.
    /// </summary>
    public class ErrorCodeTests
    {
        private static McpTool Echo() =>
            new McpTool("echo", "returns its input", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("ok")));

        [Fact]
        public void SpecAllocatedCodes_UseTheNumbersTheSpecificationChose()
        {
            // The 2026-07-28 renumbering: -32001 -> -32020, -32003 -> -32021, -32004 -> -32022.
            Assert.Equal(-32020, McpError.HeaderMismatchCode);
            Assert.Equal(-32021, McpError.MissingRequiredClientCapabilityCode);
            Assert.Equal(-32022, McpError.UnsupportedProtocolVersionCode);

            Assert.Equal(McpError.HeaderMismatchCode, McpError.HeaderMismatch("Mcp-Method", "tools/call", "tools/list").Code);
            Assert.Equal(McpError.MissingRequiredClientCapabilityCode,
                         McpError.MissingRequiredClientCapability("io.example/thing").Code);
            Assert.Equal(McpError.UnsupportedProtocolVersionCode,
                         McpError.UnsupportedProtocolVersion("1999-01-01", McpDispatcher.SupportedProtocolVersions).Code);
        }

        [Fact]
        public void TheReservedBlockIsNotOursToAllocateFrom()
        {
            Assert.False(McpError.IsImplementationDefined(McpError.HeaderMismatchCode));
            Assert.False(McpError.IsImplementationDefined(-32099));
            Assert.True(McpError.IsImplementationDefined(-32000));
            Assert.True(McpError.IsImplementationDefined(-32019));
            Assert.False(McpError.IsImplementationDefined(-32020));
        }

        [Fact]
        public void EveryCodeWeEmitIsEitherStandardJsonRpcOrSpecAllocated()
        {
            // Nothing we invent may land in the reserved block. Today we invent nothing at all: every code
            // is a standard JSON-RPC one. If that changes, it has to change into -32000..-32019.
            int[] emitted =
            {
                -32700,  // parse error (HTTP transport)
                -32601,  // method not found
                -32602,  // invalid params
                -32603,  // internal error
                McpError.HeaderMismatchCode,
                McpError.MissingRequiredClientCapabilityCode,
                McpError.UnsupportedProtocolVersionCode
            };

            foreach (int code in emitted)
            {
                bool standard = code <= -32700 || (code >= -32603 && code <= -32600);
                bool specAllocated = code >= -32099 && code <= -32020;
                Assert.True(standard || specAllocated,
                            "code " + code + " is neither standard JSON-RPC nor spec-allocated");
            }
        }

        [Fact]
        public void UnsupportedProtocolVersion_TellsTheClientWhatItCouldHaveAsked()
        {
            McpError error = McpError.UnsupportedProtocolVersion("nonsense", McpDispatcher.SupportedProtocolVersions);
            var data = (JObject)error.ErrorData;

            Assert.Equal("nonsense", (string)data["requested"]);
            Assert.Equal(McpDispatcher.SupportedProtocolVersions,
                         ((JArray)data["supported"]).Select(v => (string)v).ToArray());
        }

        [Fact]
        public void ARequestDeclaringSomethingThatIsNotARevisionIsRefused()
        {
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list",
                    ["params"] = new JObject
                    {
                        ["_meta"] = new JObject { [RequestContext.ProtocolVersionKey] = "latest" }
                    }
                });

                Assert.Equal(McpError.UnsupportedProtocolVersionCode, (int)response["error"]["code"]);
                Assert.Null(response["result"]);
            }
        }

        [Fact]
        public void ADatedRevisionWeDoNotFullyImplementIsStillServed()
        {
            // We already answer much of 2026-07-28's shape; refusing it would put those very features out of
            // reach of the only clients that ask for them. The refusal comes when the epic finishes.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                JObject response = dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list",
                    ["params"] = new JObject
                    {
                        ["_meta"] = new JObject
                        {
                            [RequestContext.ProtocolVersionKey] = RequestContext.StatelessRevision
                        }
                    }
                });

                Assert.Null(response["error"]);
                Assert.NotNull(response["result"]["tools"]);
            }
        }

        [Fact]
        public void ANotificationIsNeverAnsweredWithAnError_WhateverItDeclares()
        {
            // JSON-RPC has nowhere to put the error, and inventing an id would be worse than staying quiet.
            using (var dispatcher = new McpDispatcher(new ToolRegistry().Add(Echo())))
            {
                Assert.Null(dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized",
                    ["params"] = new JObject
                    {
                        ["_meta"] = new JObject { [RequestContext.ProtocolVersionKey] = "not-a-version" }
                    }
                }));
            }
        }
    }
}
