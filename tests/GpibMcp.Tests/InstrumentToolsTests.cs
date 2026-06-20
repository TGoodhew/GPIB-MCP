using System;
using System.Collections.Generic;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Ivi.Visa;
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
            var text = Get(new FakeInstrumentManager(), "visa_list_resources").InvokeText(new JObject());
            Assert.Equal("No VISA resources found.", text);
        }

        [Fact]
        public void ListResources_ListsEachResource()
        {
            var fake = new FakeInstrumentManager();
            fake.ResourceList.Add("GPIB0::5::INSTR");
            fake.ResourceList.Add("TCPIP0::10.0.0.1::INSTR");

            var text = Get(fake, "visa_list_resources").InvokeText(new JObject());

            Assert.Contains("Found 2", text);
            Assert.Contains("GPIB0::5::INSTR", text);
            Assert.Contains("TCPIP0::10.0.0.1::INSTR", text);
        }

        [Fact]
        public void ListResources_PhantomFullGpibBus_WarnsAboutExtender()
        {
            var fake = new FakeInstrumentManager();
            // 31 GPIB addresses present is the HP 37204A signature (it ACKs every address).
            for (int addr = 0; addr <= 30; addr++)
                fake.ResourceList.Add("GPIB0::" + addr + "::INSTR");

            var text = Get(fake, "visa_list_resources").InvokeText(new JObject());

            Assert.Contains("WARNING", text);
            Assert.Contains("37204A", text);
            Assert.Contains("which GPIB addresses are actually in use", text);
        }

        [Fact]
        public void ListResources_FewGpibInstruments_NoExtenderWarning()
        {
            var fake = new FakeInstrumentManager();
            fake.ResourceList.Add("GPIB0::9::INSTR");
            fake.ResourceList.Add("GPIB0::18::INSTR");
            fake.ResourceList.Add("GPIB0::22::INSTR");

            var text = Get(fake, "visa_list_resources").InvokeText(new JObject());

            Assert.DoesNotContain("37204A", text);
            Assert.Contains("GPIB0::9::INSTR", text);
        }

        [Fact]
        public void ListResources_ManyNonGpibResources_NoExtenderWarning()
        {
            var fake = new FakeInstrumentManager();
            // A large number of TCPIP resources must NOT trigger the GPIB-specific advisory.
            for (int i = 0; i < 25; i++)
                fake.ResourceList.Add("TCPIP0::10.0.0." + i + "::INSTR");

            var text = Get(fake, "visa_list_resources").InvokeText(new JObject());

            Assert.DoesNotContain("37204A", text);
        }

        [Fact]
        public void Query_TrimsTrailingLineEndings()
        {
            var fake = new FakeInstrumentManager();
            fake.QueryResponses["MEAS?"] = "1.234\r\n";

            var args = new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "MEAS?" };
            var text = Get(fake, "visa_query").InvokeText(args);

            Assert.Equal("1.234", text);
        }

        [Fact]
        public void Clear_IsForwardedToManager()
        {
            var fake = new FakeInstrumentManager();
            Get(fake, "visa_clear").InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR" });
            Assert.Equal("GPIB0::5::INSTR", Assert.Single(fake.Clears));
        }

        [Fact]
        public void Close_UnknownSession_ReportsNoOpenSession()
        {
            var text = Get(new FakeInstrumentManager(), "visa_close")
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR" });
            Assert.Contains("No open session", text);
        }

        [Fact]
        public void Query_MissingResource_Throws()
        {
            var tool = Get(new FakeInstrumentManager(), "visa_query");
            Assert.Throws<ArgumentException>(() => tool.InvokeText(new JObject { ["command"] = "*IDN?" }));
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
            Assert.Throws<ArgumentException>(() => tool.InvokeText(args));
        }

        [Fact]
        public void Gpib488Query_DeclaresRequiredArguments()
        {
            var tool = Get(new FakeInstrumentManager(), "gpib488_query");
            var required = (JArray)tool.InputSchema["required"];
            Assert.Contains("primary_address", required.Values<string>());
            Assert.Contains("command", required.Values<string>());
        }

        [Fact]
        public void CommandHistory_NoHistory_ReportsEmpty()
        {
            var text = Get(new FakeInstrumentManager(), "visa_command_history")
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR" });
            Assert.Contains("No command history", text);
        }

        [Fact]
        public void CommandHistory_ReturnsRecentChainForResource()
        {
            var fake = new FakeInstrumentManager();
            var registry = InstrumentTools.BuildRegistry(fake);
            registry.TryGet("visa_query", out var query);
            registry.TryGet("visa_command_history", out var history);

            query.Invoke(new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "*IDN?" });
            query.Invoke(new JObject { ["resource"] = "GPIB0::5::INSTR", ["command"] = "MEAS?" });

            var text = history.Invoke(new JObject { ["resource"] = "GPIB0::5::INSTR" }).AsText();

            Assert.Contains("Recent commands for GPIB0::5::INSTR", text);
            Assert.Contains("-> \"*IDN?\"", text);   // sent
            Assert.Contains("MEAS?", text);
            Assert.Contains("<-", text);             // a received line is present
        }

        [Fact]
        public void LastError_NoFailure_ReportsNone()
        {
            var text = Get(new FakeInstrumentManager(), "visa_last_error").InvokeText(new JObject());
            Assert.Contains("No GPIB/VISA errors", text);
        }

        [Fact]
        public void LastError_AfterFailure_ReturnsVerbatimCodesAndText()
        {
            var fake = new FakeInstrumentManager();
            var chain = new List<CommandHistoryEntry>
            {
                new CommandHistoryEntry("GPIB0::29::INSTR", CommandDirection.Sent, "*IDN?\n", DateTime.UtcNow)
            };
            fake.QueryError = GpibOperationException.For(GpibOperation.Query, "GPIB0::29::INSTR", "*IDN?",
                new NativeVisaException(unchecked((int)0xBFFF0015)), chain);

            var registry = InstrumentTools.BuildRegistry(fake);
            registry.TryGet("visa_query", out var query);
            registry.TryGet("visa_last_error", out var lastError);

            // The failing query records the error; fetching it returns the exact codes + text.
            Assert.Throws<GpibOperationException>(() =>
                query.Invoke(new JObject { ["resource"] = "GPIB0::29::INSTR", ["command"] = "*IDN?" }));

            var text = lastError.Invoke(new JObject()).AsText();
            Assert.Contains("VI_ERROR_TMO", text);
            Assert.Contains("0xBFFF0015", text);
            Assert.Contains("*IDN?", text);
        }
    }
}
