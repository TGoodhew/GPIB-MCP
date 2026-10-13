using System.Collections.Generic;

namespace Srq.Completion.Simulation
{
    /// <summary>How the simulated status byte behaves.</summary>
    public enum StatusBehaviour
    {
        /// <summary>
        /// As measured on a real HP 8563E (#142): bits latch as events - every command that completes sets
        /// command-complete, and a sweep sets command-complete and end-of-sweep when it ends. A request for
        /// service is raised when a masked bit latches, or when a mask is armed over one already latched,
        /// and the serial poll that reports a request clears the whole byte. Without a request, a poll
        /// leaves the bits alone. An error queued in the instrument reads as the error bit until cleared.
        /// </summary>
        Hp8560,

        /// <summary>
        /// The earlier model: command-complete is a level, true while idle, and a request for service
        /// re-latches whenever an armed condition is true. Kept for the legacy direct-bit flow's tests.
        /// </summary>
        LevelConditions
    }

    /// <summary>
    /// A headless, virtual-clock simulation of an 8560-style instrument's status byte, used to drive
    /// the <see cref="CompletionWaiter"/> end to end with no hardware (tests and the SrqHarness).
    ///
    /// The RQS mask and the read-back status byte share ONE layout (8560E Programming Guide, Table
    /// 7-266) in which request-service is bit 0x40 - set on every SRQ, not an error. By default the bits
    /// behave as the 8563E's do (<see cref="StatusBehaviour.Hp8560"/>). The clock advances only via
    /// <see cref="Advance"/>, so scenarios are deterministic.
    /// </summary>
    public sealed class SimulatedInstrument : IStatusChannel
    {
        // 8560-series read-back layout (8560E Programming Guide, Table 7-266), hardware-confirmed.
        public const int Message = 2, EndOfSweep = 4, CommandComplete = 16, Error = 32, RequestService = 64;

        private readonly StatusBehaviour _behaviour;
        private long _now;
        private int _mask;
        private long _sweepDoneAt = -1;
        private bool _rqsLatched;   // request-service held until a poll reads it

        // Hp8560: the latched event bits. LevelConditions: the condition bits (idle = complete, at end of sweep).
        private int _latched;
        private bool _commandComplete = true;
        private bool _endOfSweep = true;
        private bool _error;

        /// <summary>How long a triggered sweep takes (virtual ms).</summary>
        public int SweepDurationMs = 3000;

        /// <summary>Simulate an error/uncal condition that sets the error bit when the sweep completes.</summary>
        public bool ErrorOnSweep;

        /// <summary>
        /// Hp8560: an error already in the instrument's error queue (e.g. from a query it rejected) - it
        /// reads as the error bit on every poll, as it did on the 8563E in #142.
        /// </summary>
        public bool ErrorQueued;

        /// <summary>The commands the instrument received, in order (for assertions / display).</summary>
        public readonly List<string> Sent = new List<string>();

        /// <param name="initialLatched">The status bits set before the waiter starts (default: idle, after a sweep).</param>
        /// <param name="behaviour">How the status byte behaves; the real 8563E's by default.</param>
        public SimulatedInstrument(int initialLatched = (CommandComplete | EndOfSweep),
                                   StatusBehaviour behaviour = StatusBehaviour.Hp8560)
        {
            _behaviour = behaviour;
            _latched = initialLatched & ~RequestService;
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

        private bool Hp8560 => _behaviour == StatusBehaviour.Hp8560;

        private void Tick()
        {
            if (_sweepDoneAt >= 0 && _now >= _sweepDoneAt)
            {
                _sweepDoneAt = -1;
                if (Hp8560)
                {
                    Latch(CommandComplete | EndOfSweep | (ErrorOnSweep ? Error : 0));
                    return;
                }
                _commandComplete = true;
                _endOfSweep = true;
                if (ErrorOnSweep) _error = true;
            }
            if (!Hp8560) LatchRqsOnLevel();
        }

        /// <summary>Hp8560: the status bits as read - latched events plus a queued error.</summary>
        private int Status() => _latched | (ErrorQueued ? Error : 0);

        /// <summary>Hp8560: latches event bits, requesting service if any is masked.</summary>
        private void Latch(int bits)
        {
            _latched |= bits;
            if ((_mask & bits) != 0) _rqsLatched = true;
        }

        /// <summary>LevelConditions: the condition bits currently true (no request-service bit).</summary>
        private int Conditions() =>
            (_error ? Error : 0) | (_commandComplete ? CommandComplete : 0) | (_endOfSweep ? EndOfSweep : 0);

        /// <summary>LevelConditions: request-service latches whenever an armed condition is currently true.</summary>
        private void LatchRqsOnLevel()
        {
            if (_mask != 0 && (_mask & Conditions()) != 0) _rqsLatched = true;
        }

        public void Send(string command)
        {
            Sent.Add(command);
            Tick();
            foreach (var raw in command.Split(';'))
            {
                string c = raw.Trim().ToUpperInvariant();
                if (c.Length == 0) continue;
                if (c.StartsWith("RQS"))
                {
                    int n;
                    _mask = int.TryParse(c.Substring(3).Trim(), out n) ? n : 0;
                    if (Hp8560)
                    {
                        // Arming over a bit already set requests service at once; then RQS itself completes.
                        if ((_mask & Status()) != 0) _rqsLatched = true;
                        Latch(CommandComplete);
                    }
                    else LatchRqsOnLevel();   // arming a mask whose condition is already true asserts SRQ at once
                }
                else if (c == "TS")
                {
                    // Starting a sweep: busy until it ends; its command-complete comes at the end.
                    _sweepDoneAt = _now + SweepDurationMs;
                    if (!Hp8560)
                    {
                        _commandComplete = false;
                        _endOfSweep = false;
                        _error = false;
                    }
                }
                else if (Hp8560)
                {
                    // SNGLS, CONTS, MKPK HI, DONE...: complete at once - unless a sweep is still running,
                    // in which case they complete when it does (the 8560 executes commands in order).
                    if (_sweepDoneAt < 0) Latch(CommandComplete);
                }
            }
            Tick();
        }

        public int SerialPoll()
        {
            Tick();
            if (Hp8560)
            {
                // The poll that reports a request clears the whole byte; otherwise the bits stay.
                if (_rqsLatched)
                {
                    int reported = Status() | RequestService;
                    _latched = 0;
                    _rqsLatched = false;
                    return reported;
                }
                return Status();
            }

            // LevelConditions: the condition bits plus the held request-service bit, which the read clears.
            int value = Conditions() | (_rqsLatched ? RequestService : 0);
            _rqsLatched = false;
            return value;
        }
    }
}
