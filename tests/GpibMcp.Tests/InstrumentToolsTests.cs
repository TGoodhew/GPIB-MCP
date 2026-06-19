using System;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class InstrumentToolsTests
    {
        private static McpTool Get(IInstrumentManager manager, string name)
        {
            InstrumentTools.BuildRegistry(manager).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        [Fact]
        public void ListResources_NoneFound_ReportsEmpty()
        {
            var text = Get(new FakeInstrumentManager(), "visa_list_resources").Invoke(new JObject());
            Assert.Equal("No VISA resources found.", text);
        }

        [Fact]
        public void ListResources_ListsEachResource()
        {
            var fake = new FakeInstrumentManager();
            fake.ResourceList.Add("GPIB0::5::INSTR");
            fake.ResourceList.Add("TCPIP0::10.0.0.1::INSTR");

            var text = Get(fake, "visa_list_resources").Invoke(new JObject());

            Assert.Contains("Found 2", text);
            Assert.Contains("GPIB0::5::INSTR", text);
            Assert.Contains("TCPIP0::10.0.0.1::INSTR", text);
        }

        [Fact]
        public void Query_TrimsTrailingLineEndings()
        {
            var fake = new FakeInstrumentManager();
            fake.QueryResponses["MEAS?"] = "1.234\r\n";

            var args = new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "MEAS?" };
            var text = Get(fake, "visa_query").Invoke(args);

            Assert.Equal("1.234", text);
        }

        [Fact]
        public void Clear_IsForwardedToManager()
        {
            var fake = new FakeInstrumentManager();
            Get(fake, "visa_clear").Invoke(new JObject { ["resource"] = "GPIB0::5::INSTR" });
            Assert.Equal("GPIB0::5::INSTR", Assert.Single(fake.Clears));
        }

        [Fact]
        public void Close_UnknownSession_ReportsNoOpenSession()
        {
            var text = Get(new FakeInstrumentManager(), "visa_close")
                .Invoke(new JObject { ["resource"] = "GPIB0::5::INSTR" });
            Assert.Contains("No open session", text);
        }

        [Fact]
        public void Query_MissingResource_Throws()
        {
            var tool = Get(new FakeInstrumentManager(), "visa_query");
            Assert.Throws<ArgumentException>(() => tool.Invoke(new JObject { ["command"] = "*IDN?" }));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(31)]
        [InlineData(99)]
        public void Gpib488Query_PrimaryAddressOutOfRange_Throws(int primary)
        {
            // Validation must reject the address before any native NI-488.2 call is attempted.
            var tool = Get(new FakeInstrumentManager(), "gpib488_query");
            var args = new JObject { ["primary_address"] = primary, ["command"] = "*IDN?" };
            Assert.Throws<ArgumentException>(() => tool.Invoke(args));
        }

        [Fact]
        public void Gpib488Query_DeclaresRequiredArguments()
        {
            var tool = Get(new FakeInstrumentManager(), "gpib488_query");
            var required = (JArray)tool.InputSchema["required"];
            Assert.Contains("primary_address", required.Values<string>());
            Assert.Contains("command", required.Values<string>());
        }
    }
}
