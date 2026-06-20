using System;
using System.Collections.Generic;
using Srq.Completion;
using Srq.Completion.Simulation;

namespace SrqHarness
{
    /// <summary>
    /// Headless harness for the SRQ completion pattern. Runs the real <see cref="CompletionWaiter"/>
    /// against a simulated 8563E (virtual clock) and prints a live trace of every command and status
    /// poll, so the pattern can be watched and verified without hardware or Claude Desktop. The
    /// headline scenario is the 5 s sweep we have been trying on the bench. Exit code 0 = all passed.
    /// </summary>
    internal static class Program
    {
        private static int Main()
        {
            Console.WriteLine("=== SRQ completion harness - headless, simulated 8563E (virtual clock) ===");
            Console.WriteLine("Tracing every command sent and every serial poll; poll interval 500 ms.\n");

            StatusModel model = Build8563EStatusModel();
            int failures = 0;

            failures += RunScenario(
                "5 s sweep - the case under test (sweepComplete)",
                new SimulatedInstrument { SweepDurationMs = 5000 },
                model, "sweepComplete",
                expected: CompletionOutcome.Completed, minElapsedMs: 5000);

            failures += RunScenario(
                "5 s sweep, idle/complete at arm time (stale - must wait for a fresh sweep, not pre-fire)",
                new SimulatedInstrument(initialLatched: SimulatedInstrument.CommandComplete | SimulatedInstrument.EndOfSweep) { SweepDurationMs = 5000 },
                model, "sweepComplete",
                expected: CompletionOutcome.Completed, minElapsedMs: 5000);

            failures += RunScenario(
                "5 s sweep that also raises an uncal error at completion (0x50)",
                new SimulatedInstrument { SweepDurationMs = 5000, ErrorOnSweep = true },
                model, "sweepComplete",
                expected: CompletionOutcome.InstrumentError, minElapsedMs: 5000);

            failures += RunScenario(
                "sweep longer than the backstop (timeout, no hang)",
                new SimulatedInstrument { SweepDurationMs = 60000 },
                model, "sweepComplete",
                expected: CompletionOutcome.TimedOut, timeoutMs: 5000);

            Console.WriteLine(failures == 0
                ? "ALL SCENARIOS PASSED - the SRQ completion pattern works headlessly."
                : failures + " SCENARIO(S) FAILED.");
            return failures == 0 ? 0 : 1;
        }

        private static int RunScenario(string title, SimulatedInstrument sim, StatusModel model,
            string operation, CompletionOutcome expected, int timeoutMs = 30000, long minElapsedMs = 0)
        {
            Console.WriteLine("──── " + title + " ────");
            CompletionResult result = CompletionWaiter.Wait(
                model, "8563E", operation, timeoutMs, sim,
                () => sim.Now, ms => sim.Advance(ms),
                pollIntervalMs: 500, trace: line => Console.WriteLine("   " + line));

            bool ok = result.Outcome == expected && result.ElapsedMs >= minElapsedMs;
            Console.WriteLine("   commands sent: " + string.Join(" | ", sim.Sent));
            Console.WriteLine("   RESULT: " + result.Outcome + ", elapsed " + result.ElapsedMs + " ms, status 0x" +
                              result.StatusByte.ToString("X2"));
            Console.WriteLine("   EXPECT: " + expected +
                              (minElapsedMs > 0 ? " with elapsed >= " + minElapsedMs + " ms" : "") +
                              "  =>  " + (ok ? "PASS" : "FAIL") + "\n");
            return ok ? 0 : 1;
        }

        /// <summary>The 8563E status model in the hardware-confirmed read-back layout (8560E
        /// Programming Guide, Table 7-266), mirroring the DB seed. Request-service (0x40) is the
        /// completion signal; command-complete (0x10) is the armed/busy bit; error is 0x20.</summary>
        private static StatusModel Build8563EStatusModel() => new StatusModel
        {
            SrqSupported = true,
            SerialPoll = new SerialPollSpec { ClearsRqs = true },
            EnableMask = new EnableMaskSpec { SetCommand = "RQS {mask}", ClearCommand = "RQS 0", MaskFormat = "decimal" },
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
                ["sweepAndPeak"] = new StatusOperation { Arm = "SNGLS;TS;MKPK HI;DONE;", ExpectBit = "commandComplete", Restore = "CONTS;" }
            }
        };
    }
}
