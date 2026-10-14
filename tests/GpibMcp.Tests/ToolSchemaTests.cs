using System;
using System.Collections.Generic;
using System.Linq;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// The tools' schemas as stricter clients check them (#164): VS Code's Copilot refuses a whole tool whose
    /// schema has an array without <c>items</c>, which took gpib_batch away from it.
    /// </summary>
    public class ToolSchemaTests
    {
        private static Dictionary<string, JObject> ToolsList() =>
            InstrumentTools.BuildRegistry(new FakeInstrumentManager()).ToListJson().OfType<JObject>()
                .ToDictionary(t => (string)t["name"]);

        /// <summary>Every schema node, however deep, as (path, node).</summary>
        private static IEnumerable<(string, JObject)> Nodes(JToken token, string path)
        {
            if (token is JObject o)
            {
                yield return (path, o);
                foreach (var p in o.Properties())
                    foreach (var n in Nodes(p.Value, path + "." + p.Name)) yield return n;
            }
            else if (token is JArray a)
            {
                for (int i = 0; i < a.Count; i++)
                    foreach (var n in Nodes(a[i], path + "[" + i + "]")) yield return n;
            }
        }

        private static bool IsArray(JObject node)
        {
            // A 'properties' object can have a property called "type", so not every "type" is a type.
            var type = node["type"];
            if (type is JValue v) return v.Type == JTokenType.String && (string)v == "array";
            return type is JArray a && a.Any(t => t.Type == JTokenType.String && (string)t == "array");
        }

        [Fact]
        public void EveryArray_InEverySchema_SaysWhatItsItemsAre()
        {
            var missing = new List<string>();
            foreach (var tool in ToolsList())
                foreach (string schema in new[] { "inputSchema", "outputSchema" })
                    foreach (var (path, node) in Nodes(tool.Value[schema], tool.Key + "." + schema))
                        if (IsArray(node) && !(node["items"] is JObject)) missing.Add(path);

            Assert.True(missing.Count == 0, "arrays without items: " + string.Join(", ", missing));
        }

        [Fact]
        public void BatchSteps_AreDescribedAsObjects_WithTheOpsTheRunnerKnows()
        {
            var steps = ToolsList()["gpib_batch"]["inputSchema"]["properties"]["steps"];

            Assert.Equal("array", (string)steps["type"]);
            var step = (JObject)steps["items"];
            Assert.Equal("object", (string)step["type"]);
            Assert.Equal(new[] { "op" }, step["required"].Values<string>());
            Assert.Equal(new[] { "set", "write", "query", "read", "complete", "wait" },
                         step["properties"]["op"]["enum"].Values<string>());
            foreach (string key in new[] { "resource", "command", "value", "unit", "as", "operation", "settle_ms", "timeout_ms" })
                Assert.NotNull(step["properties"][key]);
            // The marker Schema uses for 'required' must not leak into what clients see.
            Assert.DoesNotContain("__required", steps.ToString());
        }

        [Fact]
        public void BatchSweep_HasItsProperties()
        {
            var sweep = ToolsList()["gpib_batch"]["inputSchema"]["properties"]["sweep"];

            Assert.Equal("object", (string)sweep["type"]);
            Assert.Equal(new[] { "var", "from", "to", "step", "count", "unit" },
                         ((JObject)sweep["properties"]).Properties().Select(p => p.Name));
        }

        [Fact]
        public void AnArray_CannotBeDeclaredWithoutItems()
        {
            Assert.Throws<ArgumentException>(() => ToolArgs.Prop("list", "array", "no items"));
            Assert.Throws<ArgumentException>(() => ToolArgs.Required("list", "array", "no items"));
        }
    }
}
