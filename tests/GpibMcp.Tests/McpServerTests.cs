using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class McpServerTests
    {
        /// <summary>Runs the server over an in-memory transport and returns the response frames.</summary>
        private static List<JObject> Run(IInstrumentManager manager, params string[] requests)
        {
            var registry = InstrumentTools.BuildRegistry(manager ?? new FakeInstrumentManager());
            var input = new StringReader(string.Join("\n", requests) + "\n");
            var output = new StringWriter();
            new McpServer(registry, input, output).Run();

            return output.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(JObject.Parse)
                .ToList();
        }

        private static string Init(string protocolVersion = "2025-06-18") =>
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"" +
            protocolVersion + "\",\"capabilities\":{},\"clientInfo\":{\"name\":\"test\",\"version\":\"1.0\"}}}";

        [Fact]
        public void Initialize_ReturnsServerInfoAndCapabilities()
        {
            var responses = Run(null, Init());
            var result = responses.Single()["result"];

            Assert.Equal(McpServer.ServerName, (string)result["serverInfo"]["name"]);
            Assert.Equal(McpServer.ServerVersion, (string)result["serverInfo"]["version"]);
            Assert.NotNull(result["capabilities"]["tools"]);
        }

        [Fact]
        public void Initialize_EchoesClientProtocolVersion()
        {
            var responses = Run(null, Init("2024-11-05"));
            Assert.Equal("2024-11-05", (string)responses.Single()["result"]["protocolVersion"]);
        }

        [Fact]
        public void Initialize_FallsBackToServerProtocolWhenUnspecified()
        {
            var request = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":{}}}";
            var responses = Run(null, request);
            Assert.Equal(McpServer.ProtocolVersion, (string)responses.Single()["result"]["protocolVersion"]);
        }

        [Fact]
        public void ToolsList_ReturnsAllToolsWithSchemas()
        {
            var responses = Run(null, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
            var tools = (JArray)responses.Single()["result"]["tools"];

            var names = tools.Select(t => (string)t["name"]).ToList();
            Assert.Equal(16, names.Count);
            Assert.Contains("visa_list_resources", names);
            Assert.Contains("visa_query", names);
            Assert.Contains("gpib488_query", names);
            Assert.Contains("instrument_list_models", names);
            Assert.Contains("assign_instrument", names);
            Assert.Contains("instrument_reference", names);

            // Every advertised tool must carry an object input schema.
            Assert.All(tools, t => Assert.Equal("object", (string)t["inputSchema"]["type"]));
        }

        [Fact]
        public void ToolsList_VisaQueryDeclaresRequiredArguments()
        {
            var responses = Run(null, "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}");
            var tools = (JArray)responses.Single()["result"]["tools"];
            var query = tools.Single(t => (string)t["name"] == "visa_query");

            var required = ((JArray)query["inputSchema"]["required"]).Select(x => (string)x).ToList();
            Assert.Contains("resource", required);
            Assert.Contains("command", required);
            Assert.DoesNotContain("timeout_ms", required);
        }

        [Fact]
        public void Ping_ReturnsEmptyResult()
        {
            var responses = Run(null, "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"ping\"}");
            var response = responses.Single();
            Assert.Equal(7, (int)response["id"]);
            Assert.NotNull(response["result"]);
            Assert.Empty((JObject)response["result"]);
        }

        [Fact]
        public void UnknownMethod_ReturnsMethodNotFound()
        {
            var responses = Run(null, "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"does/not/exist\"}");
            Assert.Equal(-32601, (int)responses.Single()["error"]["code"]);
        }

        [Fact]
        public void ToolsCall_UnknownTool_ReturnsInvalidParams()
        {
            var request = "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\"," +
                          "\"params\":{\"name\":\"no_such_tool\",\"arguments\":{}}}";
            Assert.Equal(-32602, (int)Run(null, request).Single()["error"]["code"]);
        }

        [Fact]
        public void ToolsCall_Identify_ReturnsInstrumentResponse()
        {
            var fake = new FakeInstrumentManager();
            fake.QueryResponses["*IDN?"] = "ACME,Model5,SN1,1.0";

            var request = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\"," +
                          "\"params\":{\"name\":\"visa_identify\",\"arguments\":{\"resource\":\"GPIB0::5::INSTR\"}}}";
            var result = Run(fake, request).Single()["result"];

            Assert.Null(result["isError"]);
            Assert.Equal("ACME,Model5,SN1,1.0", (string)result["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_MissingRequiredArgument_ReturnsIsErrorResult()
        {
            // Missing "command" => handler throws => MCP reports a result with isError=true,
            // NOT a JSON-RPC transport error.
            var request = "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\"," +
                          "\"params\":{\"name\":\"visa_query\",\"arguments\":{\"resource\":\"GPIB0::5::INSTR\"}}}";
            var result = Run(null, request).Single()["result"];

            Assert.True((bool)result["isError"]);
            Assert.StartsWith("Error:", (string)result["content"][0]["text"]);
        }

        [Fact]
        public void ToolsCall_Write_IsForwardedToManager()
        {
            var fake = new FakeInstrumentManager();
            var request = "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\"," +
                          "\"params\":{\"name\":\"visa_write\",\"arguments\":" +
                          "{\"resource\":\"GPIB0::5::INSTR\",\"command\":\"*RST\"}}}";

            var result = Run(fake, request).Single()["result"];

            Assert.Null(result["isError"]);
            Assert.Equal("GPIB0::5::INSTR|*RST", Assert.Single(fake.Writes));
        }

        [Fact]
        public void MalformedLine_IsSkipped_AndSubsequentRequestStillHandled()
        {
            var responses = Run(null, "{ this is not json", "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"ping\"}");
            Assert.Equal(8, (int)responses.Single()["id"]);
        }

        [Fact]
        public void Notification_ProducesNoResponse()
        {
            // A message without an id is a notification; the server must not reply to it.
            var responses = Run(null,
                "{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}",
                "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"ping\"}");

            Assert.Single(responses);
            Assert.Equal(10, (int)responses[0]["id"]);
        }

        // ---- image content -------------------------------------------------------

        /// <summary>Runs the server with a caller-supplied registry (for custom/image tools).</summary>
        private static List<JObject> RunRegistry(ToolRegistry registry, params string[] requests)
        {
            var input = new StringReader(string.Join("\n", requests) + "\n");
            var output = new StringWriter();
            new McpServer(registry, input, output).Run();
            return output.ToString()
                .Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(JObject.Parse)
                .ToList();
        }

        private static string CallTool(string name) =>
            "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"" + name + "\",\"arguments\":{}}}";

        [Fact]
        public void ToolsCall_ImageOutput_ReturnsImageContentBlock()
        {
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };
            var registry = new ToolRegistry().Add(new McpTool(
                "snap", "returns an image", new JObject { ["type"] = "object" },
                _ => ToolOutput.Image(png, "image/png")));

            var content = (JArray)RunRegistry(registry, Init(), CallTool("snap")).Last()["result"]["content"];

            var image = content.Single();
            Assert.Equal("image", (string)image["type"]);
            Assert.Equal("image/png", (string)image["mimeType"]);
            Assert.Equal(png, Convert.FromBase64String((string)image["data"])); // round-trips to the bytes
        }

        [Fact]
        public void ToolsCall_TextThenImage_ReturnsBothBlocksInOrder()
        {
            byte[] png = { 0x89, 0x50, 0x4E, 0x47, 9, 9 };
            var registry = new ToolRegistry().Add(new McpTool(
                "snap2", "caption + image", new JObject { ["type"] = "object" },
                _ => ToolOutput.Image(png, "image/png", caption: "8563E screen")));

            var content = (JArray)RunRegistry(registry, Init(), CallTool("snap2")).Last()["result"]["content"];

            Assert.Equal(2, content.Count);
            Assert.Equal("text", (string)content[0]["type"]);
            Assert.Equal("8563E screen", (string)content[0]["text"]);
            Assert.Equal("image", (string)content[1]["type"]);
        }

        [Fact]
        public void ToolsCall_StringTool_StillReturnsTextBlock()
        {
            // Back-compat: a plain string handler still yields a single text content block.
            var registry = new ToolRegistry().Add(new McpTool(
                "say", "text tool", new JObject { ["type"] = "object" }, _ => "hello"));

            var content = (JArray)RunRegistry(registry, Init(), CallTool("say")).Last()["result"]["content"];
            Assert.Equal("text", (string)content.Single()["type"]);
            Assert.Equal("hello", (string)content.Single()["text"]);
        }
    }
}
