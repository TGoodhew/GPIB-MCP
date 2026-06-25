using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Ivi.Visa;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class McpServerTests
    {
        static McpServerTests()
        {
            // The always-on tool-call audit log fires for every tools/call through the server; keep it out
            // of the real %LOCALAPPDATA% during tests.
            Environment.SetEnvironmentVariable("GPIB_MCP_TOOL_CALL_LOG",
                Path.Combine(Path.GetTempPath(), "gpibmcp-test-tool-calls.log"));
        }

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
        public void Initialize_OmitsInstructions_WhenNotProvided()
        {
            // The bare constructor (used by most tests) supplies no instructions.
            Assert.Null(Run(null, Init()).Single()["result"]["instructions"]);
        }

        [Fact]
        public void Initialize_IncludesInstructions_WhenProvided()
        {
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
            var input = new StringReader(Init() + "\n");
            var output = new StringWriter();
            new McpServer(registry, input, output, "CAPABILITY SUMMARY HERE").Run();

            var result = JObject.Parse(output.ToString().Trim())["result"];
            Assert.Equal("CAPABILITY SUMMARY HERE", (string)result["instructions"]);
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
            Assert.Equal(29, names.Count);
            Assert.Contains("gpib_batch", names);
            Assert.Contains("visa_write_raw", names);
            Assert.Contains("print_capture_to_windows", names);
            Assert.Contains("resolve_setting", names);
            Assert.Contains("gpib_overview", names);
            Assert.Contains("visa_list_resources", names);
            Assert.Contains("instrument_db_refresh", names);
            Assert.Contains("set_termination", names);
            Assert.Contains("visa_query", names);
            Assert.Contains("gpib488_query", names);
            Assert.Contains("instrument_list_models", names);
            Assert.Contains("assign_instrument", names);
            Assert.Contains("instrument_reference", names);
            Assert.Contains("instrument_capture_screen", names);
            Assert.Contains("visa_command_history", names);
            Assert.Contains("visa_last_error", names);
            Assert.Contains("visa_serial_poll", names);
            Assert.Contains("visa_wait_srq", names);
            Assert.Contains("instrument_wait_complete", names);

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
        public void ToolsCall_WritesOneAuditLineToTheToolCallLog()
        {
            var fake = new FakeInstrumentManager();
            fake.QueryResponses["*IDN?"] = "ACME,Model5,SN1,1.0";

            string logPath = Path.Combine(Path.GetTempPath(), "gpibmcp_audit_" + Path.GetRandomFileName() + ".log");
            string prev = Environment.GetEnvironmentVariable("GPIB_MCP_TOOL_CALL_LOG");
            Environment.SetEnvironmentVariable("GPIB_MCP_TOOL_CALL_LOG", logPath);
            try
            {
                var request = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\"," +
                              "\"params\":{\"name\":\"visa_identify\",\"arguments\":{\"resource\":\"GPIB0::5::INSTR\"}}}";
                Run(fake, request);

                string[] lines = File.ReadAllLines(logPath);
                Assert.Single(lines);                                  // exactly one audit line for one tools/call
                Assert.Contains("visa_identify", lines[0]);
                Assert.Contains("ok", lines[0]);
                Assert.Contains("resource=GPIB0::5::INSTR", lines[0]); // args digest is captured
            }
            finally
            {
                Environment.SetEnvironmentVariable("GPIB_MCP_TOOL_CALL_LOG", prev);
                try { File.Delete(logPath); } catch { /* best effort */ }
            }
        }

        [Fact]
        public void RepeatedSingleOpCalls_AppendABatchNudge_ToTheResult()
        {
            // #74: soft steering didn't move tool selection; the server nudges in the result the model reads.
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
            string write(int id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\"," +
                "\"params\":{\"name\":\"visa_write\",\"arguments\":{\"resource\":\"GPIB0::5::INSTR\",\"command\":\"FR" + id + "MH\"}}}";
            var input = new StringReader(write(1) + "\n" + write(2) + "\n");
            var output = new StringWriter();
            new McpServer(registry, input, output, null, new GpibMcp.Tools.BatchLoopNudge(threshold: 2)).Run();

            var responses = output.ToString()
                .Split(new[] { '\n' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(JObject.Parse).ToList();

            string ResultText(int i) => string.Concat(responses[i]["result"]["content"].Select(c => (string)c["text"]));
            Assert.DoesNotContain("gpib_batch", ResultText(0));   // first call: below threshold, no nudge
            Assert.Contains("gpib_batch", ResultText(1));         // second call hits the threshold: nudge appended
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
        public void ToolsCall_GpibFailure_RendersDecodedStatusAndCommandChain()
        {
            // A GPIB/VISA failure must come back as an isError result that names the resource,
            // the decoded VISA status, and the recent command chain - not a bare exception string.
            var fake = new FakeInstrumentManager();
            var chain = new List<CommandHistoryEntry>
            {
                new CommandHistoryEntry("GPIB0::18::INSTR", CommandDirection.Sent, "MKPK HI?\n", DateTime.UtcNow)
            };
            var inner = new NativeVisaException(unchecked((int)0xBFFF0015));
            fake.QueryError = GpibOperationException.For(GpibOperation.Query, "GPIB0::18::INSTR", "MKPK HI?",
                inner, chain, VisaErrorInfo.Describe(inner));

            var request = "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\"," +
                          "\"params\":{\"name\":\"visa_query\",\"arguments\":" +
                          "{\"resource\":\"GPIB0::18::INSTR\",\"command\":\"MKPK HI?\"}}}";
            var result = Run(fake, request).Single()["result"];

            Assert.True((bool)result["isError"]);
            string text = (string)result["content"][0]["text"];
            Assert.Contains("GPIB0::18::INSTR", text);
            Assert.Contains("VI_ERROR_TMO", text);
            Assert.Contains("Recent command chain", text);
            Assert.Contains("MKPK HI?", text);
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
