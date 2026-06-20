using System.Collections.Generic;
using Srq.Completion;
using Srq.Completion.Simulation;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Headless end-to-end tests for the completion state machine, driving the real
    /// <see cref="CompletionWaiter"/> against <see cref="SimulatedInstrument"/> with a virtual clock.
    /// The simulator and model mirror the behaviour confirmed on a real 8563E (see
    /// srq-8560-dual-bit-layout): the RQS mask and the read-back status byte share one layout, in which
    /// request-service is bit 0x40 (set on every SRQ, not an error), and command-complete is a condition
    /// that is true while idle - so the waiter must wait for the operation to go BUSY before accepting
    /// the next service request as the real completion. No hardware.
    /// </summary>
    public class CompletionWaiterTests
    {
        // The 8563E status model in the hardware-confirmed read-back layout (Table 7-266).
        private static StatusModel Model8563() => new StatusModel
        {
            SrqSupported = true,
            SerialPoll = new SerialPollSpec { ClearsRqs = true },
            EnableMask = new EnableMaskSpec { SetCommand = "RQS {mask}", ClearCommand = "RQS 0" },
            DoneSupport = new DoneSupportSpec { Supported = true, Mnemonic = "DONE" },
            ErrorBit = "error",
            RequestServiceBit = "requestService",
            Bits = new Dictionary<string, int>
            {
                ["message"] = 2, ["endOfSweep"] = 4, ["commandComplete"] = 16,
                ["error"] = 32, ["requestService"] = 64
            },
            Operations = new Dictionary<string, StatusOperation>
            {
                ["sweepComplete"] = new StatusOperation { Arm = "SNGLS;TS;", ExpectBit = "commandComplete", Restore = "CONTS;" },
                ["sweepAndPeak"] = new StatusOperation { Arm = "SNGLS;TS;MKPK HI;DONE;", ExpectBit = "commandComplete" }
            }
        };

        private static CompletionResult Run(StatusModel model, string op, int timeoutMs, SimulatedInstrument sim, int poll = 50) =>
            CompletionWaiter.Wait(model, "8563E", op, timeoutMs, sim, () => sim.Now, ms => sim.Advance(ms), poll);

        [Fact]
        public void SweepComplete_WaitsForRealCompletion_ViaRequestService()
        {
            // Idle at arm time (command-complete already true) - the stale case. The busy handshake must
            // wait for the FRESH sweep, not read the just-armed condition as a finished operation.
            var sim = new SimulatedInstrument { SweepDurationMs = 3000 };
            var result = Run(Model8563(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.RequestService, result.StatusByte & SimulatedInstrument.RequestService);
            Assert.InRange(result.ElapsedMs, 3000, 3150);    // the full sweep, not ~0 (stale) or the backstop
            Assert.Equal(0, result.StatusByte & SimulatedInstrument.Error);

            Assert.Contains("RQS 0", sim.Sent);    // disarmed before arming
            Assert.Contains("RQS 48", sim.Sent);   // mask = commandComplete|error = 16|32 (NOT request-service)
            Assert.Contains("SNGLS;TS;", sim.Sent);
            Assert.Contains("CONTS;", sim.Sent);   // restored
        }

        [Fact]
        public void ErrorDuringSweep_ReportsInstrumentError()
        {
            // The sweep finishes (service requested) but an uncal/error is also set -> error bit at completion.
            var sim = new SimulatedInstrument { SweepDurationMs = 2000, ErrorOnSweep = true };
            var result = Run(Model8563(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.InstrumentError, result.Outcome);
            Assert.Equal(SimulatedInstrument.Error, result.StatusByte & SimulatedInstrument.Error);
            Assert.InRange(result.ElapsedMs, 2000, 2150);    // still waited for the real completion
            Assert.Contains("signalled an ERROR", result.Message);
        }

        [Fact]
        public void NeverCompletes_TimesOutAtBackstop()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 100000 }; // longer than the timeout
            var result = Run(Model8563(), "sweepComplete", 3000, sim);

            Assert.Equal(CompletionOutcome.TimedOut, result.Outcome);
            Assert.InRange(result.ElapsedMs, 3000, 3150);
            Assert.Contains("RQS 0", sim.Sent);              // mask cleared, bus usable
        }

        [Fact]
        public void SweepAndPeak_CompletesViaRequestService()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 1500 };
            var result = Run(Model8563(), "sweepAndPeak", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.RequestService, result.StatusByte & SimulatedInstrument.RequestService);
            Assert.Contains("SNGLS;TS;MKPK HI;DONE;", sim.Sent);
        }

        // ---- legacy direct-bit flow (models without a requestServiceBit, e.g. the 3325) ----------

        [Fact]
        public void DirectBit_Model_PollsExpectBitDirectly()
        {
            var model = Model8563();
            model.RequestServiceBit = null;   // no request-service bit -> legacy direct-bit flow
            var sim = new SimulatedInstrument { SweepDurationMs = 2000 };
            var result = Run(model, "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.CommandComplete, result.StatusByte & SimulatedInstrument.CommandComplete);
            Assert.InRange(result.ElapsedMs, 2000, 2150);
        }

        // ---- dispatch states (no I/O) -------------------------------------------

        [Fact]
        public void NoStatusModel_NeedsDefinition()
        {
            var sim = new SimulatedInstrument();
            var result = Run(null, "sweepComplete", 5000, sim);
            Assert.Equal(CompletionOutcome.NeedsDefinition, result.Outcome);
            Assert.Empty(sim.Sent); // nothing sent to the instrument
        }

        [Fact]
        public void SrqUnsupported_Refuses()
        {
            var sim = new SimulatedInstrument();
            var result = Run(new StatusModel { SrqSupported = false }, "sweepComplete", 5000, sim);
            Assert.Equal(CompletionOutcome.Refused, result.Outcome);
            Assert.Empty(sim.Sent);
        }

        [Fact]
        public void UnknownOperation_NeedsDefinition_ListsKnownOps()
        {
            var sim = new SimulatedInstrument();
            var result = Run(Model8563(), "bogus", 5000, sim);
            Assert.Equal(CompletionOutcome.NeedsDefinition, result.Outcome);
            Assert.Contains("sweepComplete", result.Message);
            Assert.Empty(sim.Sent);
        }
    }
}
