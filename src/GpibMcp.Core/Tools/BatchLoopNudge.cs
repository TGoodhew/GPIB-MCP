using System;
using System.Collections.Generic;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Detects a run of single-op GPIB tool calls - the per-point measurement loop that should have been one
    /// <c>gpib_batch</c> - and produces a nudge to append to the tool result, where the model reliably reads
    /// it (#74). Soft steering in the always-loaded instructions and gpib_overview did not change tool
    /// selection on the bench; this lands the suggestion at the point of decision, in context the model
    /// definitely sees. Stateful across calls for the life of the server/session; the run resets the moment
    /// the model actually batches.
    /// </summary>
    public sealed class BatchLoopNudge
    {
        /// <summary>Per-instrument I/O tools that make up a per-point loop (gpib_batch is the intended
        /// replacement). Non-loop tools - overview, list, assign, define - are ignored: neither counted nor
        /// reset, so an occasional lookup mid-sweep doesn't mask the loop.</summary>
        private static readonly HashSet<string> SingleOpTools = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "visa_write", "visa_query", "visa_read", "visa_identify",
            "instrument_identify", "visa_clear", "instrument_wait_complete"
        };

        private readonly int _threshold;
        private readonly int _repeatEvery;
        private int _run;

        /// <param name="threshold">Emit the first nudge once this many single-op GPIB calls have run in a row.</param>
        /// <param name="repeatEvery">After the threshold, re-emit every Nth call (the model ignored the soft steering, so remind it).</param>
        public BatchLoopNudge(int threshold = 10, int repeatEvery = 4)
        {
            _threshold = threshold < 1 ? 1 : threshold;
            _repeatEvery = repeatEvery < 1 ? 1 : repeatEvery;
        }

        /// <summary>Current single-op run length (for tests/inspection).</summary>
        public int Run => _run;

        /// <summary>
        /// Observe a tool call by name; returns a nudge string to append to its result, or null. A
        /// <c>gpib_batch</c> call resets the run (the model did the right thing); single-op GPIB calls
        /// accumulate; any other tool is ignored.
        /// </summary>
        public string Observe(string toolName)
        {
            if (string.Equals(toolName, "gpib_batch", StringComparison.OrdinalIgnoreCase)) { _run = 0; return null; }
            if (toolName == null || !SingleOpTools.Contains(toolName)) return null;

            _run++;
            if (_run < _threshold || (_run - _threshold) % _repeatEvery != 0) return null;

            return "NOTE (gpib-mcp): that was " + _run + " single-op GPIB calls in a row with no gpib_batch. If this " +
                   "is a sweep or a repeated per-point measurement - ESPECIALLY one touching several instruments per " +
                   "point - STOP now and use gpib_batch for the remaining points: it expands the sweep and runs the " +
                   "ordered per-point steps (set/write/query/complete, across instruments, with {{var}}/{{capture}} " +
                   "interpolation) server-side and returns one table. Continuing one call per point makes the per-call " +
                   "round-trip dominate and can exhaust the tool-call budget mid-run.";
        }
    }
}
