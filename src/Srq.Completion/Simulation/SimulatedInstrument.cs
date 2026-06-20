using System.Collections.Generic;

namespace Srq.Completion.Simulation
{
    /// <summary>
    /// A headless, virtual-clock simulation of an 8560-style instrument's status byte, used to drive
    /// the <see cref="CompletionWaiter"/> end to end with no hardware (tests and the SrqHarness).
    ///
    /// It models the behaviour confirmed on a real 8563E (see srq-8560-dual-bit-layout): the RQS mask
    /// and the read-back status byte share ONE layout (Table 7-266) in which request-service is bit
    /// 0x40 - set on every SRQ, not an error. Command-complete is a CONDITION that is true while the
    /// instrument is idle (so arming it asserts SRQ immediately), goes false when a sweep starts (TS),
    /// and true again when the sweep finishes; an uncal/error sets the error bit at completion. The RQS
    /// bit reflects "an armed condition is currently true" and a serial poll reads (and momentarily
    /// clears) it. The clock advances only via <see cref="Advance"/>, so scenarios are deterministic.
    /// </summary>
    public sealed class SimulatedInstrument : IStatusChannel
    {
        // 8560-series read-back layout (8560E Programming Guide, Table 7-266), hardware-confirmed.
        public const int Message = 2, EndOfSweep = 4, CommandComplete = 16, Error = 32, RequestService = 64;

        private long _now;
        private int _mask;
        private long _sweepDoneAt = -1;
        private bool _donePending;
        private bool _rqsLatched;   // request-service held until read (so a stale SRQ can pre-fire an arm)

        // Condition state. Idle = last command complete and sitting at end of sweep.
        private bool _commandComplete = true;
        private bool _endOfSweep = true;
        private bool _error;

        /// <summary>How long a triggered sweep takes (virtual ms).</summary>
        public int SweepDurationMs = 3000;

        /// <summary>Simulate an error/uncal condition that sets the error bit when the sweep completes.</summary>
        public bool ErrorOnSweep;

        /// <summary>The commands the instrument received, in order (for assertions / display).</summary>
        public readonly List<string> Sent = new List<string>();

        /// <param name="initialLatched">
        /// Seeds the idle condition bits before the waiter starts (e.g. <see cref="CommandComplete"/> to
        /// reproduce "armed while already complete", the stale case the busy handshake must survive).
        /// </param>
        public SimulatedInstrument(int initialLatched = (CommandComplete | EndOfSweep))
        {
            _commandComplete = (initialLatched & CommandComplete) != 0;
            _endOfSweep = (initialLatched & EndOfSweep) != 0;
            _error = (initialLatched & Error) != 0;
        }

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
                _commandComplete = true;
                _endOfSweep = true;
                if (ErrorOnSweep) _error = true;
                if (_donePending) _donePending = false;
                _sweepDoneAt = -1;
            }
            LatchRqs();
        }

        /// <summary>The condition bits currently true (no request-service bit).</summary>
        private int Conditions() =>
            (_error ? Error : 0) | (_commandComplete ? CommandComplete : 0) | (_endOfSweep ? EndOfSweep : 0);

        /// <summary>Request-service latches whenever an armed (masked) condition is currently true.</summary>
        private void LatchRqs()
        {
            if (_mask != 0 && (_mask & Conditions()) != 0) _rqsLatched = true;
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
                    LatchRqs();   // arming a mask whose condition is already true asserts SRQ at once
                }
                else if (c == "TS")
                {
                    // Starting a sweep clears the completion conditions (instrument goes BUSY) and
                    // schedules a fresh completion - exactly the transition the busy handshake relies on.
                    _commandComplete = false;
                    _endOfSweep = false;
                    _error = false;
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
            // The read returns the condition bits plus the held request-service bit (GPIB RQS, 0x40),
            // then clears the held RQS - reflecting current conditions on the next read.
            int value = Conditions() | (_rqsLatched ? RequestService : 0);
            _rqsLatched = false;
            return value;
        }
    }
}
