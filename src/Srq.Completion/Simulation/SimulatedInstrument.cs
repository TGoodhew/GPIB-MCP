using System.Collections.Generic;

namespace Srq.Completion.Simulation
{
    /// <summary>
    /// A headless, virtual-clock simulation of an 8560-style instrument's status byte, used to drive
    /// the <see cref="CompletionWaiter"/> end to end with no hardware (tests and the SrqHarness). It
    /// models the behaviours that matter for completion logic: a serial poll reads and CLEARS the
    /// latched bits; starting a sweep (TS) clears END OF SWEEP and schedules a fresh completion; DONE
    /// makes COMMAND COMPLETE assert when the sweep finishes; and the clock only advances via
    /// <see cref="Advance"/> (driven by the waiter's injected sleep), so scenarios are deterministic.
    /// </summary>
    public sealed class SimulatedInstrument : IStatusChannel
    {
        // 8560-series weights (8560E Programming Guide, Table 7-9).
        public const int Trigger = 4, Message = 8, EndOfSweep = 16, CommandComplete = 32, Error = 64, Rqs = 128;

        private long _now;
        private int _latched;
        private int _mask;
        private long _sweepDoneAt = -1;
        private bool _donePending;

        /// <summary>How long a triggered sweep takes (virtual ms).</summary>
        public int SweepDurationMs = 3000;

        /// <summary>Simulate an error/uncal condition that sets the error bit when the sweep completes.</summary>
        public bool ErrorOnSweep;

        /// <summary>The commands the instrument received, in order (for assertions / display).</summary>
        public readonly List<string> Sent = new List<string>();

        public SimulatedInstrument(int initialLatched = 0) { _latched = initialLatched; }

        /// <summary>The virtual clock (ms). Advances only via <see cref="Advance"/>.</summary>
        public long Now => _now;

        /// <summary>The current SRQ enable mask the instrument was last told (for display).</summary>
        public int Mask => _mask;

        /// <summary>Advances the virtual clock (the waiter's sleep calls this) and updates pending events.</summary>
        public void Advance(int ms)
        {
            if (ms > 0) _now += ms;
            Tick();
        }

        private void Tick()
        {
            if (_sweepDoneAt >= 0 && _now >= _sweepDoneAt)
            {
                _latched |= EndOfSweep;
                if (ErrorOnSweep) _latched |= Error;
                if (_donePending) { _latched |= CommandComplete; _donePending = false; }
                _sweepDoneAt = -1;
            }
        }

        public void Send(string command)
        {
            Sent.Add(command);
            foreach (var raw in command.Split(';'))
            {
                string c = raw.Trim().ToUpperInvariant();
                if (c.Length == 0) continue;
                if (c.StartsWith("RQS"))
                {
                    int n;
                    _mask = int.TryParse(c.Substring(3).Trim(), out n) ? n : 0;
                }
                else if (c == "TS")
                {
                    // Starting a sweep clears END OF SWEEP / COMMAND COMPLETE then schedules a fresh
                    // completion - exactly the behaviour that produced the 0x00 hardware read.
                    _latched &= ~(EndOfSweep | CommandComplete);
                    _sweepDoneAt = _now + SweepDurationMs;
                }
                else if (c == "DONE")
                {
                    _donePending = true;
                }
                // SNGLS / CONTS / MKPK HI etc. have no status-byte effect in this model.
            }
        }

        public int SerialPoll()
        {
            Tick();
            int value = _latched;
            _latched = 0; // serial poll clears the latched event bits
            return value;
        }
    }
}
