using System;
using GpibMcp.Diagnostics;

namespace GpibMcp.Mcp
{
    /// <summary>
    /// The per-call channel back to the caller while a tool is still running (#112). A tool that takes
    /// seconds - a screen capture, a batch sweep - calls <see cref="Progress"/> at its natural milestones
    /// instead of going silent; where that lands depends on how the call was made:
    ///
    /// <list type="bullet">
    /// <item>a normal <c>tools/call</c> with a <c>_meta.progressToken</c> → a <c>notifications/progress</c>
    ///       message on the transport's outbound channel;</item>
    /// <item>a call the server turned into a task → the task's <c>statusMessage</c>, which the client sees
    ///       on its next <c>tasks/get</c> poll;</item>
    /// <item>neither → nothing at all, at near-zero cost.</item>
    /// </list>
    ///
    /// Tools therefore never ask which mode they are in. The reporter is supplied by the dispatcher.
    /// </summary>
    public sealed class ToolCallContext
    {
        /// <summary>A context that reports nowhere - for direct tool invocation (tests, internal calls).</summary>
        public static readonly ToolCallContext None = new ToolCallContext(null);

        private readonly Action<double, double?, string> _report;
        private readonly object _gate = new object();
        private double _lastProgress = double.NegativeInfinity;
        private volatile bool _cancelRequested;

        /// <param name="report">
        /// Receives (progress, total, message), or <c>null</c> to discard progress entirely.
        /// </param>
        public ToolCallContext(Action<double, double?, string> report)
        {
            _report = report;
        }

        /// <summary>True when progress actually goes somewhere - lets a tool skip work it only does to report.</summary>
        public bool ProgressWanted => _report != null;

        /// <summary>
        /// Reports one progress milestone. <paramref name="progress"/> must increase across the call: a value
        /// that does not is dropped, because the spec requires monotonic progress and a stray report is worth
        /// less than a conforming stream.
        /// </summary>
        /// <param name="progress">Work done so far, on any scale (with or without a total).</param>
        /// <param name="total">Total work, when known.</param>
        /// <param name="message">Human-readable description of the current step.</param>
        public void Progress(double progress, double? total, string message)
        {
            if (_report == null) return;
            lock (_gate)
            {
                if (progress <= _lastProgress) return;
                _lastProgress = progress;
            }
            // Reporting must never take a tool down: it is diagnostics, not the job.
            try { _report(progress, total, message); }
            catch (Exception ex) { Log.Debug("Progress report failed: " + ex.Message); }
        }

        /// <summary>
        /// Set when the client asked to cancel the task running this call. Cancellation is cooperative and
        /// today no tool observes it mid-flight - a GPIB read is a blocking driver call - so a task already
        /// executing runs to completion, which the tasks extension explicitly allows.
        /// </summary>
        public bool CancellationRequested => _cancelRequested;

        /// <summary>Records the client's cancellation request. Called by the task runner.</summary>
        public void RequestCancellation() { _cancelRequested = true; }
    }
}
