using System.Collections.Generic;
using Srq.Completion;
using Srq.Completion.Simulation;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Headless end-to-end tests for the completion state machine, driving the real
    /// <see cref="CompletionWaiter"/> against <see cref="SimulatedInstrument"/> with a virtual clock.
    /// The simulator behaves as a real 8563E was measured to (#142): the RQS mask and the read-back status
    /// byte share one layout, in which request-service is bit 0x40 (set on every SRQ, not an error); bits
    /// latch as events, so every command that completes sets command-complete; arming over a bit already
    /// set requests service at once; and the poll that reports a request clears the whole byte. No hardware.
    /// </summary>
    public class CompletionWaiterTests
    {
        // The 8563E status model as shipped (data/instruments/8563E.json), in the Table 7-266 layout.
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
                ["sweepComplete"] = new StatusOperation { Prepare = "SNGLS;", Arm = "TS;", ExpectBit = "commandComplete", Restore = "CONTS;" },
                ["sweepAndPeak"] = new StatusOperation { Prepare = "SNGLS;", Arm = "TS;MKPK HI;", ExpectBit = "commandComplete", Restore = "CONTS;" }
            }
        };

        private static CompletionResult Run(StatusModel model, string op, int timeoutMs, SimulatedInstrument sim, int poll = 50) =>
            CompletionWaiter.Wait(model, "8563E", op, timeoutMs, sim, () => sim.Now, ms => sim.Advance(ms), poll);

        [Fact]
        public void SweepComplete_WaitsForTheSweep_NotTheRequestThatArmingRaises()
        {
            // Idle at arm time: command-complete is already set, so arming the mask requests service at once.
            // That request must be drained, and the wait must last the whole sweep.
            var sim = new SimulatedInstrument { SweepDurationMs = 3000 };
            var result = Run(Model8563(), "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(SimulatedInstrument.RequestService, result.StatusByte & SimulatedInstrument.RequestService);
            Assert.Equal(SimulatedInstrument.EndOfSweep, result.StatusByte & SimulatedInstrument.EndOfSweep);
            Assert.InRange(result.ElapsedMs, 3000, 3150);    // the full sweep, not ~0 (stale) or the backstop
            Assert.Equal(0, result.StatusByte & SimulatedInstrument.Error);

            // Disarm, then the mode command BEFORE arming (its command-complete must not count), then the sweep.
            Assert.Equal(new[] { "RQS 0", "SNGLS;", "RQS 48", "TS;", "RQS 0", "CONTS;" }, sim.Sent);
        }

        [Fact]
        public void FastSweep_FinishesBeforeTheFirstPollAfterIt_Completes()
        {
            // #142: the 8563E's default auto-coupled sweep is 50 ms - shorter than the poll interval, so the
            // sweep is over before the instrument is ever seen busy. It used to wait out the backstop.
            var sim = new SimulatedInstrument { SweepDurationMs = 50 };
            var result = Run(Model8563(), "sweepComplete", 5000, sim, poll: 100);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.True(result.ElapsedMs < 300, "completed after " + result.ElapsedMs + " ms");
        }

        [Fact]
        public void ZeroDurationOperation_Completes()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 0 };
            var result = Run(Model8563(), "sweepComplete", 5000, sim, poll: 100);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.True(result.ElapsedMs < 300, "completed after " + result.ElapsedMs + " ms");
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

        /// <summary>The simulator, behind a channel that can also clear its error queue, as an 8563E's can (ERR?).</summary>
        private sealed class ClearableInstrument : IStatusChannel, IErrorQueueChannel
        {
            public readonly SimulatedInstrument Sim;
            public int Clears;
            public ClearableInstrument(SimulatedInstrument sim) { Sim = sim; }
            public void Send(string command) => Sim.Send(command);
            public int SerialPoll() => Sim.SerialPoll();
            public IReadOnlyList<string> ClearErrors()
            {
                Clears++;
                if (!Sim.ErrorQueued) return new string[0];
                Sim.ErrorQueued = false;
                return new[] { "112" };
            }
        }

        private static CompletionResult Run(StatusModel model, string op, int timeoutMs, ClearableInstrument channel, int poll = 50) =>
            CompletionWaiter.Wait(model, "8563E", op, timeoutMs, channel, () => channel.Sim.Now, ms => channel.Sim.Advance(ms), poll);

        [Fact]
        public void ErrorQueuedBeforeTheOperation_IsClearedFirst_ThenTheOperationCompletes()
        {
            // #142 run 2: *IDN?, which the 8563E rejects, left an error queued, and the error bit read at the end
            // of a sweep that had worked was reported as the sweep's error. Cleared before starting, it isn't.
            var channel = new ClearableInstrument(new SimulatedInstrument { SweepDurationMs = 50, ErrorQueued = true });
            var result = Run(Model8563(), "sweepComplete", 5000, channel, poll: 100);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.Equal(1, channel.Clears);
            Assert.Equal(0, result.StatusByte & SimulatedInstrument.Error);
            Assert.Contains("Cleared error 112", result.Message);
        }

        [Fact]
        public void ErrorQueuedBeforeTheOperation_DoesNotHideANewErrorDuringIt()
        {
            // Copilot on #143: excusing an error bit that was already set would also hide a failure of the
            // operation itself. Clearing the old error first leaves the new one to be reported.
            var channel = new ClearableInstrument(new SimulatedInstrument { SweepDurationMs = 50, ErrorQueued = true, ErrorOnSweep = true });
            var result = Run(Model8563(), "sweepComplete", 5000, channel, poll: 100);

            Assert.Equal(CompletionOutcome.InstrumentError, result.Outcome);
            Assert.Contains("Cleared error 112", result.Message);
        }

        [Fact]
        public void ErrorQueuedBeforeTheOperation_ThatCannotBeCleared_IsAnErrorThatMayBeTheOldOne()
        {
            // No way to read the queue (no identity.errorQuery): one error bit can't tell old from new, so it is
            // not passed off as success - it is an error, flagged as possibly the earlier one.
            var sim = new SimulatedInstrument { SweepDurationMs = 50, ErrorQueued = true };
            var result = Run(Model8563(), "sweepComplete", 5000, sim, poll: 100);

            Assert.Equal(CompletionOutcome.InstrumentError, result.Outcome);
            Assert.Contains("may be that earlier error", result.Message);
        }

        [Fact]
        public void NeverCompletes_TimesOutAtBackstop_AndSaysWhatItSaw()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 100000 }; // longer than the timeout
            var result = Run(Model8563(), "sweepComplete", 3000, sim);

            Assert.Equal(CompletionOutcome.TimedOut, result.Outcome);
            Assert.InRange(result.ElapsedMs, 3000, 3150);
            Assert.Contains("Last polls: 0x00@", result.Message);   // the polls that led to the timeout
            Assert.Equal("RQS 0", sim.Sent[sim.Sent.Count - 2]);    // mask cleared, bus usable
        }

        [Fact]
        public void SweepAndPeak_CompletesViaRequestService()
        {
            var sim = new SimulatedInstrument { SweepDurationMs = 1500 };
            var result = Run(Model8563(), "sweepAndPeak", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.InRange(result.ElapsedMs, 1500, 1650);
            Assert.Contains("TS;MKPK HI;", sim.Sent);
        }

        [Fact]
        public void ModeCommandInTheArm_CompletesAtOnce_WhichIsWhyItBelongsInPrepare()
        {
            // The shape every 8560-family statusModel had before #142. On the real instrument SNGLS completes
            // at once and requests service, so with it in the arm the "completion" is SNGLS's, not the sweep's.
            // InstrumentDatabase replaces such unedited user copies with the bundled block (see supersedes).
            var model = Model8563();
            model.Operations["sweepComplete"] = new StatusOperation { Arm = "SNGLS;TS;", ExpectBit = "commandComplete", Restore = "CONTS;" };
            var sim = new SimulatedInstrument { SweepDurationMs = 3000 };
            var result = Run(model, "sweepComplete", 30000, sim);

            Assert.Equal(CompletionOutcome.Completed, result.Outcome);
            Assert.True(result.ElapsedMs < 3000, "the sweep had not finished: " + result.ElapsedMs + " ms");
            Assert.Equal(0, result.StatusByte & SimulatedInstrument.EndOfSweep);
        }

        // ---- legacy direct-bit flow (models without a requestServiceBit, e.g. the 3325) ----------

        [Fact]
        public void DirectBit_Model_PollsExpectBitDirectly()
        {
            var model = Model8563();
            model.RequestServiceBit = null;   // no request-service bit -> legacy direct-bit flow
            model.Operations["sweepComplete"] = new StatusOperation { Arm = "SNGLS;TS;", ExpectBit = "commandComplete", Restore = "CONTS;" };
            var sim = new SimulatedInstrument(behaviour: StatusBehaviour.LevelConditions) { SweepDurationMs = 2000 };
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
