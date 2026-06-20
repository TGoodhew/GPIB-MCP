using System.Collections.Generic;
using GpibMcp.Instruments;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>Tests for the SRQ/serial-poll primitives and status-bit decoding (issues #14-#16).</summary>
    public class SrqTests
    {
        // 8560-series status model (manual-correct bit weights, Table 7-9).
        private static StatusModel Sa8560() => new StatusModel
        {
            SrqSupported = true,
            SerialPoll = new SerialPollSpec { ClearsRqs = true },
            EnableMask = new EnableMaskSpec { SetCommand = "RQS {mask}", ClearCommand = "RQS 0" },
            Bits = new Dictionary<string, int>
            {
                ["trigger"] = 4, ["message"] = 8, ["endOfSweep"] = 16,
                ["commandComplete"] = 32, ["error"] = 64, ["rqs"] = 128
            }
        };

        private static InstrumentDefinition Def8563() => new InstrumentDefinition
        {
            Model = "8563E",
            Identity = new IdentitySpec { Command = "ID?", MatchRegex = "8563" },
            StatusModel = Sa8560(),
            Commands = new List<InstrumentCommand>()
        };

        private static McpTool Tool(IInstrumentManager visa, InstrumentDatabase db, AssignmentStore store, string name)
        {
            InstrumentTools.BuildRegistry(visa, db, store).TryGet(name, out var tool);
            Assert.NotNull(tool);
            return tool;
        }

        // ---- bit decoding --------------------------------------------------------

        [Fact]
        public void SetBitNames_DecodesNamedBitsHighestFirst()
        {
            // 0xB0 = 176 = rqs(128) + commandComplete(32) + endOfSweep(16)
            var names = Sa8560().SetBitNames(0xB0);
            Assert.Equal(new[] { "rqs (0x80)", "commandComplete (0x20)", "endOfSweep (0x10)" }, names);
        }

        [Fact]
        public void SetBitNames_NoBitsSet_ReturnsEmpty()
        {
            Assert.Empty(Sa8560().SetBitNames(0));
        }

        [Fact]
        public void BitValue_KnownAndUnknown()
        {
            Assert.Equal(16, Sa8560().BitValue("endOfSweep"));
            Assert.Null(Sa8560().BitValue("nope"));
        }

        // ---- visa_serial_poll ----------------------------------------------------

        [Fact]
        public void SerialPoll_AssignedModel_BreaksDownNamedBits()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Def8563() });
            var store = AssignmentStore.InMemory();
            store.Set("GPIB0::18::INSTR", "8563E");
            var visa = new FakeInstrumentManager { StatusByteValue = 0xB0 };

            var text = Tool(visa, db, store, "visa_serial_poll")
                .InvokeText(new JObject { ["resource"] = "GPIB0::18::INSTR" });

            Assert.Contains("176 (0xB0)", text);
            Assert.Contains("rqs (0x80)", text);
            Assert.Contains("commandComplete (0x20)", text);
            Assert.Contains("clears RQS", text);
            Assert.Equal("GPIB0::18::INSTR", Assert.Single(visa.SerialPolls));
        }

        [Fact]
        public void SerialPoll_NoModel_FallsBackToStandardBits()
        {
            var db = InstrumentDatabase.Empty();
            var visa = new FakeInstrumentManager { StatusByteValue = 0x40 }; // RQS in the IEEE position

            var text = Tool(visa, db, AssignmentStore.InMemory(), "visa_serial_poll")
                .InvokeText(new JObject { ["resource"] = "GPIB0::5::INSTR" });

            Assert.Contains("64 (0x40)", text);
            Assert.Contains("RQS(0x40)", text);
            Assert.Contains("no statusModel", text);
        }

        // ---- visa_wait_srq -------------------------------------------------------

        [Fact]
        public void WaitSrq_Asserted_ReportsElapsed()
        {
            var visa = new FakeInstrumentManager { SrqResult = new SrqWaitResult(true, 1234) };
            var text = Tool(visa, InstrumentDatabase.Empty(), AssignmentStore.InMemory(), "visa_wait_srq")
                .InvokeText(new JObject { ["resource"] = "GPIB0::18::INSTR", ["timeout_ms"] = 5000 });

            Assert.Contains("SRQ asserted", text);
            Assert.Contains("1234 ms", text);
            Assert.Equal("GPIB0::18::INSTR|5000", Assert.Single(visa.SrqWaits));
        }

        [Fact]
        public void WaitSrq_TimedOut_ReportsDistinctResult()
        {
            var visa = new FakeInstrumentManager { SrqResult = new SrqWaitResult(false, 3000) };
            var text = Tool(visa, InstrumentDatabase.Empty(), AssignmentStore.InMemory(), "visa_wait_srq")
                .InvokeText(new JObject { ["resource"] = "GPIB0::18::INSTR", ["timeout_ms"] = 3000 });

            Assert.Contains("No SRQ", text);
            Assert.Contains("within 3000 ms", text);
        }
    }
}
