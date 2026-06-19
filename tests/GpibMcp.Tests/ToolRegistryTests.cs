using System;
using System.Linq;
using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class ToolRegistryTests
    {
        private static McpTool Tool(string name) =>
            new McpTool(name, "desc", new JObject { ["type"] = "object" }, _ => "ok");

        [Fact]
        public void Add_DuplicateName_Throws()
        {
            var registry = new ToolRegistry().Add(Tool("dup"));
            Assert.Throws<InvalidOperationException>(() => registry.Add(Tool("dup")));
        }

        [Fact]
        public void TryGet_ReturnsRegisteredTool()
        {
            var registry = new ToolRegistry().Add(Tool("a"));
            Assert.True(registry.TryGet("a", out var tool));
            Assert.Equal("a", tool.Name);
        }

        [Fact]
        public void TryGet_UnknownName_ReturnsFalse()
        {
            Assert.False(new ToolRegistry().TryGet("missing", out _));
        }

        [Fact]
        public void TryGet_NullName_ReturnsFalseWithoutThrowing()
        {
            Assert.False(new ToolRegistry().TryGet(null, out _));
        }

        [Fact]
        public void ToListJson_PreservesInsertionOrder()
        {
            var registry = new ToolRegistry().Add(Tool("first")).Add(Tool("second")).Add(Tool("third"));
            var names = registry.ToListJson().Select(t => (string)t["name"]).ToList();
            Assert.Equal(new[] { "first", "second", "third" }, names);
        }
    }
}
