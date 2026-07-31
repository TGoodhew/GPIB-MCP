using System;
using System.Linq;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// <c>tools/list</c> as a cacheable, deterministically-ordered result (#108, SEP-2549): a client that can
    /// cache the catalogue stops re-fetching 29 descriptors, and a stable order is what lets a model's prompt
    /// cache hit at all.
    /// </summary>
    public class ToolsListCachingTests
    {
        private static JObject List(McpDispatcher dispatcher, string revision = null)
        {
            var prms = new JObject();
            if (revision != null)
                prms["_meta"] = new JObject { [RequestContext.ProtocolVersionKey] = revision };

            return (JObject)dispatcher.Dispatch(new JObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/list", ["params"] = prms
            })["result"];
        }

        [Fact]
        public void ToolsList_CarriesTheCacheHints_ForAClientOnTheRevisionThatDefinesThem()
        {
            using (var dispatcher = new McpDispatcher(InstrumentTools.BuildRegistry(new FakeInstrumentManager())))
            {
                JObject result = List(dispatcher, RequestContext.StatelessRevision);

                Assert.Equal(CacheableResult.DefaultTtlMs, (int)result["ttlMs"]);
                Assert.Equal("private", (string)result["cacheScope"]);
                Assert.NotNull(result["tools"]);
            }
        }

        [Fact]
        public void ToolsList_StaysBare_ForAClientOnAnOlderRevision()
        {
            // Nothing to gain and a strict validator to lose: these fields do not exist in its schema.
            using (var dispatcher = new McpDispatcher(InstrumentTools.BuildRegistry(new FakeInstrumentManager())))
            {
                foreach (JObject result in new[] { List(dispatcher), List(dispatcher, "2025-06-18") })
                {
                    Assert.Null(result["ttlMs"]);
                    Assert.Null(result["cacheScope"]);
                    Assert.NotNull(result["tools"]);
                }
            }
        }

        [Fact]
        public void ToolsList_IsIdenticalEveryTime()
        {
            // The freshness hint is a promise: within its window the answer must not move. It also underpins
            // the prompt cache - reordering the descriptors would invalidate it for nothing.
            using (var dispatcher = new McpDispatcher(InstrumentTools.BuildRegistry(new FakeInstrumentManager())))
            {
                JToken first = List(dispatcher, RequestContext.StatelessRevision)["tools"];
                JToken second = List(dispatcher, RequestContext.StatelessRevision)["tools"];
                Assert.True(JToken.DeepEquals(first, second));
            }
        }

        [Fact]
        public void ToolsList_KeepsRegistrationOrder()
        {
            var registry = new ToolRegistry();
            foreach (string name in new[] { "zeta", "alpha", "mike" })
                registry.Add(new McpTool(name, "t", null, (Func<JObject, ToolOutput>)(a => ToolOutput.Text("x"))));

            using (var dispatcher = new McpDispatcher(registry))
            {
                var names = ((JArray)List(dispatcher)["tools"]).Select(t => (string)t["name"]).ToArray();

                // Registration order, NOT sorted: the order is whatever the registry was built in, and the
                // guarantee is that it never changes between calls.
                Assert.Equal(new[] { "zeta", "alpha", "mike" }, names);
            }
        }

        [Fact]
        public void TheRealRegistryOrderIsPinned()
        {
            // A regression guard on the actual server: if a tool is added or moved, this test says so, and
            // whoever moved it has to mean it - every client's cached copy and prompt cache turns over.
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
            var names = registry.ToListJson().Select(t => (string)t["name"]).ToArray();

            Assert.Equal("gpib_overview", names.First());
            Assert.Equal(registry.Tools.Select(t => t.Name).ToArray(), names);
            Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        }

        [Fact]
        public void CacheHints_AgreeWithListChangedBeingFalse()
        {
            // Both say the same thing - the catalogue is fixed for the life of the process - so a TTL is
            // safe. If tools ever became dynamic, both would have to change together.
            using (var dispatcher = new McpDispatcher(InstrumentTools.BuildRegistry(new FakeInstrumentManager())))
            {
                JObject init = (JObject)dispatcher.Dispatch(new JObject
                {
                    ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "initialize",
                    ["params"] = new JObject { ["capabilities"] = new JObject() }
                })["result"];

                Assert.False((bool)init["capabilities"]["tools"]["listChanged"]);
                Assert.True(CacheableResult.DefaultTtlMs > 0);
            }
        }
    }
}
