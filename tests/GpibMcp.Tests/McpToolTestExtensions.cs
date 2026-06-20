using GpibMcp.Mcp;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Test convenience: invoke a tool and get its concatenated text output. Tool handlers now
    /// return a <see cref="ToolOutput"/> (text and/or image blocks); these tests assert on text.
    /// </summary>
    internal static class McpToolTestExtensions
    {
        public static string InvokeText(this McpTool tool, JObject args) => tool.Invoke(args).AsText();
    }
}
