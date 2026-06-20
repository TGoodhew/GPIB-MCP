using System.Collections.Generic;
using GpibMcp.Instruments;
using GpibMcp.Instruments.Completion;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Headless end-to-end tests for the completion state machine, driving the real
    /// <see cref="CompletionWaiter"/> against <see cref="SimulatedInstrument"/> with a virtual clock.
    /// No hardware - this is the harness for iterating on the SRQ/completion logic.
    /// </summary>
    public class CompletionWaiterTests
    {
        private static StatusModel Model8560() => new StatusModel
        {
            SrqSupported = true,
            SerialPoll = new SerialPollSpec { ClearsRqs = true },
            EnableMask = new EnableMaskSpec { SetCommand = "RQS {mask}", ClearCommand = "RQS 0" },
            DoneSupport = new DoneSupportSpec { Supported = true, Mnemonic = "DONE" },
            ErrorBit = "error",
            Bits = new Dictionary<string, int>
            {
                ["trigger"] = 4, ["message"] = 8, ["endOfSweep"] = 16,
                ["commandComplete"] = 32, ["error"] = 64, ["rqs"] = 128
            },
            Operations = new Dictionary<string, StatusOperation>
            {
                ["sweepComplete"] = new StatusOperation { Arm = "SNGLS;TS;", ExpectBit = "endOfSweep", Restore = "CONTS;" },
                ["sweepAndPeak"] = new StatusOperation { Arm = "SNGLS;TS;MKPK HI;DONE;", ExpectBit = "commandComplete" }
            }
        };

        private static CompletionResult Run(StatusModel model, string op, int timeoutMs, SimulatedInstrument sim, int poll = 50) =>
            CompletionWaiter.Wait(model, "8563E", op, timeoutMs, sim, () => sim.Now, ms => sim.Advance(ms), poll);

        [Fact]
        public void SweepComplete_ReturnsOnActualCompletion_NotAGuess()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 3000 };
            var result = Run(Model8560(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.EndOfSweep, result.StatusByte & SimulatedInstrument.EndOfSweep);
            // Returned at the real sweep duration (within one poll interval), not instantly or at the backstop.
            Assert.InRange(result.ElapsedMs, 3000, 3100);
            Assert.Contains("RQS 80", sim.Sent);   // mask = endOfSweep|error = 16|64
            Assert.Contains("RQS 0", sim.Sent);     // mask cleared
            Assert.Contains("CONTS;", sim.Sent);    // restored
        }

        [Fact]
        public void StaleEndOfSweep_IsPreCleared_StillWaitsForFreshSweep()
        {
            // Regression for the hardware bug: a stale END OF SWEEP latched from a prior sweep must NOT
            // be mistaken for completion. With the pre-clear, the wait still takes the full sweep time.
            var sim = new SimulatedInstrument(initialLatched: SimulatedInstrument.EndOfSweep) { SweepDurationMs = 3000 };
            var result = Run(Model8560(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.InRange(result.ElapsedMs, 3000, 3100);   // waited for the FRESH sweep, not the stale bit (~0 ms)
        }

        [Fact]
        public void ErrorDuringSweep_ReportsInstrumentError_AndNotesCompletion()
        {
            // The exact hardware case: the sweep finishes (END OF SWEEP) but an uncal error is also set -> 0x50.
            var sim = new SimulatedInstrument { SweepDurationMs = 2000, ErrorOnSweep = true };
            var result = Run(Model8560(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.InstrumentError, result.Outcome);
            Assert.Equal(0x50, result.StatusByte);          // endOfSweep(16) | error(64)
            Assert.Contains("completion bit is also set", result.Message);
        }

        [Fact]
        public void NeverCompletes_TimesOutAtBackstop()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 100000 }; // longer than the timeout
            var result = Run(Model8560(), "sweepComplete", 3000, sim);

            Assert.Equal(CompletionOutcome.TimedOut, result.Outcome);
            Assert.InRange(result.ElapsedMs, 3000, 3100);
            Assert.Contains("RQS 0", sim.Sent);             // mask still cleared, bus usable
        }

        [Fact]
        public void SweepAndPeak_CompletesOnCommandComplete()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 1500 };
            var result = Run(Model8560(), "sweepAndPeak", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.CommandComplete,
                result.StatusByte & SimulatedInstrument.CommandComplete);
            Assert.Contains("RQS 96", sim.Sent);            // mask = commandComplete|error = 32|64
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
            var result = Run(Model8560(), "bogus", 5000, sim);
            Assert.Equal(CompletionOutcome.NeedsDefinition, result.Outcome);
            Assert.Contains("sweepComplete", result.Message);
            Assert.Empty(sim.Sent);
        }
    }
}
