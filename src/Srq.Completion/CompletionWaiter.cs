using System;
using System.Collections.Generic;
using System.Globalization;

namespace Srq.Completion
{
    /// <summary>How an <see cref="CompletionResult"/> turned out.</summary>
    public enum CompletionOutcome
    {
        /// <summary>The expected status bit was seen - the operation completed.</summary>
        Completed,
        /// <summary>The instrument's error/fail bit was seen.</summary>
        InstrumentError,
        /// <summary>The backstop timeout elapsed before completion.</summary>
        TimedOut,
        /// <summary>The model declares no SRQ support - refused (no timed fallback).</summary>
        Refused,
        /// <summary>The statusModel/operation is missing or incomplete - prompt for definitions.</summary>
        NeedsDefinition
    }

    /// <summary>The outcome of an <see cref="CompletionWaiter.Wait"/>: outcome + message + status detail.</summary>
    public sealed class CompletionResult
    {
        public CompletionOutcome Outcome { get; }
        public string Message { get; }
        public int StatusByte { get; }
        public long ElapsedMs { get; }
        public IReadOnlyList<string> SetBits { get; }

        public CompletionResult(CompletionOutcome outcome, string message, int statusByte,
                                long elapsedMs, IReadOnlyList<string> setBits)
        {
            Outcome = outcome;
            Message = message;
            StatusByte = statusByte;
            ElapsedMs = elapsedMs;
            SetBits = setBits ?? Array.Empty<string>();
        }

        internal static CompletionResult Dispatch(CompletionOutcome outcome, string message) =>
            new CompletionResult(outcome, message, 0, 0, Array.Empty<string>());
    }

    /// <summary>
    /// The data-driven, hardware-agnostic completion state machine. Decoupled from VISA and the MCP
    /// layer via <see cref="IStatusChannel"/> and injected clock/sleep so it can be unit- and harness-
    /// tested headlessly. 3-state dispatch (refuse / needs-definition / run); the run flow pre-clears
    /// stale status, sends any prepare commands, arms the SRQ mask, starts the operation, and confirms
    /// completion by polling the LATCHED status byte (bits stay set until read), then clears the mask
    /// and restores.
    /// </summary>
    public static class CompletionWaiter
    {
        public const int DefaultPollIntervalMs = 100;
        public const int DefaultTimeoutMs = 30000;

        /// <summary>
        /// SRQ-edge: pause (ms) after arming the mask before draining, so the instrument has executed the
        /// prepare and arm commands - and raised any service request they cause - before it is polled.
        /// </summary>
        public const int ArmSettleMs = 20;

        /// <summary>
        /// SRQ-edge: most polls the drain after arming may spend consuming service requests. One is normal
        /// (the poll that reports a request clears it); the bound stops a condition that keeps requesting
        /// from holding up the wait.
        /// </summary>
        public const int MaxArmDrainPolls = 5;

        /// <summary>How many of the last polls a timed-out result lists, to show what the wait saw.</summary>
        public const int RecentPollsShown = 6;

        /// <param name="trace">Optional sink that receives a line per phase/poll, for live tracing.</param>
        public static CompletionResult Wait(StatusModel model, string modelName, string operationName,
            int timeoutMs, IStatusChannel channel, Func<long> nowMs, Action<int> sleep,
            int pollIntervalMs = DefaultPollIntervalMs, Action<string> trace = null)
        {
            Action<string> log = trace ?? (_ => { });

            // ---- dispatch ----------------------------------------------------
            if (model == null)
                return CompletionResult.Dispatch(CompletionOutcome.NeedsDefinition,
                    PromptMissing(modelName, operationName, "has no statusModel defined"));
            if (!model.SrqSupported)
                return CompletionResult.Dispatch(CompletionOutcome.Refused,
                    "Model '" + modelName + "' declares no SRQ support (srqSupported=false). This tool will not " +
                    "fall back to a timed guess - use a timed query if appropriate.");

            StatusOperation op;
            if (model.Operations == null || !model.Operations.TryGetValue(operationName, out op))
                return CompletionResult.Dispatch(CompletionOutcome.NeedsDefinition, PromptOperation(modelName, operationName, model));
            int? expect = model.BitValue(op.ExpectBit);
            if (expect == null)
                return CompletionResult.Dispatch(CompletionOutcome.NeedsDefinition,
                    PromptMissing(modelName, operationName,
                        "operation '" + operationName + "' expects bit '" + op.ExpectBit + "', not defined in statusModel.bits"));
            if (model.EnableMask == null || string.IsNullOrEmpty(model.EnableMask.SetCommand))
                return CompletionResult.Dispatch(CompletionOutcome.NeedsDefinition,
                    PromptMissing(modelName, operationName, "statusModel.enableMask.setCommand is missing"));

            // ---- run ---------------------------------------------------------
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;
            if (pollIntervalMs <= 0) pollIntervalMs = DefaultPollIntervalMs;

            int? errorBit = model.BitValue(model.ErrorBit);          // model-named error/fail bit
            int? rqsBit = model.BitValue(model.RequestServiceBit);   // GPIB request-service bit (0x40), if modelled

            return rqsBit.HasValue
                ? RunSrqEdge(model, modelName, operationName, op, expect.Value, errorBit, rqsBit.Value,
                             timeoutMs, channel, nowMs, sleep, pollIntervalMs, log)
                : RunDirectBit(model, modelName, operationName, op, expect.Value, errorBit,
                               timeoutMs, channel, nowMs, sleep, pollIntervalMs, log);
        }

        /// <summary>
        /// SRQ flow, hardware-confirmed on the 8563E. The RQS mask and the read-back status byte share
        /// one layout (e.g. 8560 Table 7-266): request-service is bit 0x40 - set on EVERY service request,
        /// NOT an error - so completion is detected by that bit, and the error bit is a separate condition.
        ///
        /// On the 8563E the status bits latch as events (every command that completes sets
        /// command-complete), arming a mask whose condition is already set requests service at once, and
        /// the poll that reports a request clears the whole byte (#142). So: disarm and drain; send the
        /// operation's prepare commands (e.g. SNGLS;) while nothing is armed; arm the mask and drain the
        /// request that arming raises; then start the operation (e.g. TS;). The first request after that
        /// is the operation's, however soon it comes - a 50 ms sweep can finish before the first poll.
        /// </summary>
        private static CompletionResult RunSrqEdge(StatusModel model, string modelName, string operationName,
            StatusOperation op, int expect, int? errorBit, int rqsBit, int timeoutMs, IStatusChannel channel,
            Func<long> nowMs, Action<int> sleep, int pollIntervalMs, Action<string> log)
        {
            // The arm mask deliberately EXCLUDES the request-service bit (arming it self-fires).
            int mask = expect | (errorBit ?? 0);
            long start = nowMs();
            var polls = new RecentPolls();
            log("run '" + operationName + "' on " + modelName + " [SRQ-edge]: expect '" + op.ExpectBit + "'=0x" +
                expect.ToString("X2") + ", errorBit '" + (model.ErrorBit ?? "-") + "'=0x" + (errorBit ?? 0).ToString("X2") +
                ", requestService '" + model.RequestServiceBit + "'=0x" + rqsBit.ToString("X2") +
                ", mask=" + mask + " (0x" + mask.ToString("X2") + "), timeout=" + timeoutMs + "ms");

            // Disarm + drain, so nothing left from earlier can request service once armed. An error bit
            // seen before the operation starts was not caused by it (e.g. a query the instrument rejected).
            SafeSend(channel, model.EnableMask.ClearCommand, "disarm", log);
            int drained = channel.SerialPoll();
            log("drain serial poll -> 0x" + drained.ToString("X2"));
            bool errorBefore = HasBit(drained, errorBit);

            // One error bit can't say whether an error is old or new, so clear an old one before starting,
            // when the instrument's error queue can be read; then an error at completion is the operation's.
            string note = null;
            if (errorBefore && channel is IErrorQueueChannel queue)
            {
                try
                {
                    IReadOnlyList<string> cleared = queue.ClearErrors();
                    int after = channel.SerialPoll();
                    log("cleared the error queue (" + string.Join(", ", cleared) + ") -> 0x" + after.ToString("X2"));
                    errorBefore = HasBit(after, errorBit);
                    if (!errorBefore && cleared.Count > 0)
                        note = "Cleared error " + string.Join(", ", cleared) + ", already queued before '" + operationName + "' started.";
                }
                catch (Exception ex) { log("could not clear the error queue: " + ex.Message); }
            }

            // Mode commands go before arming: they complete at once, and a command-complete they raise
            // with the mask armed would be indistinguishable from the operation's own.
            if (!string.IsNullOrEmpty(op.Prepare)) { channel.Send(op.Prepare); log("send (prepare): " + op.Prepare); }

            string setCmd = model.EnableMask.SetCommand.Replace("{mask}", mask.ToString(CultureInfo.InvariantCulture));
            channel.Send(setCmd); log("send (arm mask): " + setCmd);

            // Arming a condition that is already set requests service at once; drain that request so the
            // next one belongs to the operation.
            sleep(ArmSettleMs);
            for (int i = 0; i < MaxArmDrainPolls; i++)
            {
                int pre = channel.SerialPoll();
                log("drain after arming -> 0x" + pre.ToString("X2"));
                if (HasBit(pre, errorBit)) errorBefore = true;
                if ((pre & rqsBit) == 0) break;
            }

            if (!string.IsNullOrEmpty(op.Arm)) { channel.Send(op.Arm); log("send (start op): " + op.Arm); }

            int stb = 0;
            long elapsed = 0;
            while (true)
            {
                stb = channel.SerialPoll();
                elapsed = nowMs() - start;
                polls.Add(stb, elapsed);
                log("poll @ " + elapsed + "ms -> 0x" + stb.ToString("X2"));
                if ((stb & rqsBit) != 0) break;        // the first request after the start: done (or errored)
                if (elapsed >= timeoutMs) break;       // backstop
                sleep(pollIntervalMs);
            }

            SafeSend(channel, model.EnableMask.ClearCommand, "clear mask", log);
            SafeSend(channel, op.Restore, "restore", log);

            bool serviced = (stb & rqsBit) != 0;
            bool err = HasBit(stb, errorBit);
            if (err && errorBefore)
            {
                // Still an error, but possibly not a new one - say so rather than guess either way.
                note = "The error bit was already set before '" + operationName + "' started and could not be " +
                       "cleared, so this may be that earlier error rather than a new one - read the instrument's " +
                       "error queue (e.g. ERR?) to tell.";
            }
            return Finish(model, operationName, op, stb, elapsed, timeoutMs, serviced, err, log, note, polls);
        }

        /// <summary>
        /// Legacy direct-bit flow: arm the mask (expect|error) and poll the status byte until the expect
        /// (or error) bit appears. Used when the model does not declare a <see cref="StatusModel.RequestServiceBit"/>.
        /// </summary>
        private static CompletionResult RunDirectBit(StatusModel model, string modelName, string operationName,
            StatusOperation op, int expect, int? errorBit, int timeoutMs, IStatusChannel channel,
            Func<long> nowMs, Action<int> sleep, int pollIntervalMs, Action<string> log)
        {
            int mask = expect | (errorBit ?? 0);
            long start = nowMs();
            log("run '" + operationName + "' on " + modelName + ": expect '" + op.ExpectBit + "'=0x" +
                expect.ToString("X2") + ", errorBit '" + (model.ErrorBit ?? "-") + "'=0x" + (errorBit ?? 0).ToString("X2") +
                ", mask=" + mask + " (0x" + mask.ToString("X2") + "), timeout=" + timeoutMs + "ms");

            // Pre-clear any STALE latched status (e.g. an END OF SWEEP left set by a prior sweep) so the
            // just-armed mask cannot fire on old state and we wait for a FRESH completion.
            int stale = channel.SerialPoll();
            log("pre-clear serial poll -> 0x" + stale.ToString("X2"));

            if (!string.IsNullOrEmpty(op.Prepare)) { channel.Send(op.Prepare); log("send (prepare): " + op.Prepare); }

            string setCmd = model.EnableMask.SetCommand.Replace("{mask}", mask.ToString(CultureInfo.InvariantCulture));
            channel.Send(setCmd); log("send (arm mask): " + setCmd);
            if (!string.IsNullOrEmpty(op.Arm)) { channel.Send(op.Arm); log("send (start op): " + op.Arm); }

            int stb = 0;
            long elapsed = 0;
            var polls = new RecentPolls();
            while (true)
            {
                stb = channel.SerialPoll();
                elapsed = nowMs() - start;
                polls.Add(stb, elapsed);
                log("poll @ " + elapsed + "ms -> 0x" + stb.ToString("X2"));
                if ((stb & mask) != 0) break;          // completion or error bit appeared
                if (elapsed >= timeoutMs) break;       // backstop
                sleep(pollIntervalMs);
            }

            SafeSend(channel, model.EnableMask.ClearCommand, "clear mask", log);
            SafeSend(channel, op.Restore, "restore", log);

            bool done = (stb & expect) == expect;
            bool err = HasBit(stb, errorBit);
            return Finish(model, operationName, op, stb, elapsed, timeoutMs, done, err, log, null, polls);
        }

        /// <summary>Builds the final result + message from the terminal status byte (shared by both flows).</summary>
        private static CompletionResult Finish(StatusModel model, string operationName, StatusOperation op,
            int stb, long elapsed, int timeoutMs, bool done, bool err, Action<string> log, string note, RecentPolls polls)
        {
            IReadOnlyList<string> bits = model.SetBitNames(stb);
            string detail = "status byte " + stb + " (0x" + stb.ToString("X2") + ") [" + Describe(bits) + "]";

            CompletionResult result;
            if (err)
                result = new CompletionResult(CompletionOutcome.InstrumentError,
                    "Instrument signalled an ERROR during '" + operationName + "' after " + elapsed + " ms - " + detail +
                    (done ? " (the expected completion bit is also set)" : "") + "." +
                    (note != null ? " " + note : ""), stb, elapsed, bits);
            else if (done)
                result = new CompletionResult(CompletionOutcome.Completed,
                    "Completed '" + operationName + "' after " + elapsed + " ms - " + detail + "." +
                    (note != null ? " " + note : ""), stb, elapsed, bits);
            else
                result = new CompletionResult(CompletionOutcome.TimedOut,
                    "Timed out after " + timeoutMs + " ms waiting for '" + operationName + "' (expected bit '" +
                    op.ExpectBit + "' not set) - " + detail + ". Last polls: " + polls + ". Mask cleared; bus left usable.",
                    stb, elapsed, bits);

            log("=> " + result.Outcome + ": " + result.Message);
            return result;
        }

        private static bool HasBit(int stb, int? bit) => bit.HasValue && bit.Value != 0 && (stb & bit.Value) == bit.Value;

        /// <summary>The last few serial polls of a wait, as "0x54@212ms", for a timed-out result.</summary>
        private sealed class RecentPolls
        {
            private readonly Queue<string> _last = new Queue<string>();

            public void Add(int stb, long elapsedMs)
            {
                _last.Enqueue("0x" + stb.ToString("X2") + "@" + elapsedMs + "ms");
                if (_last.Count > RecentPollsShown) _last.Dequeue();
            }

            public override string ToString() => _last.Count == 0 ? "none" : string.Join(", ", _last);
        }

        private static void SafeSend(IStatusChannel channel, string command, string what, Action<string> log)
        {
            if (string.IsNullOrEmpty(command)) return;
            try { channel.Send(command); log("send (" + what + "): " + command); }
            catch { /* best effort */ }
        }

        private static string Describe(IReadOnlyList<string> bits) =>
            bits.Count > 0 ? string.Join(", ", bits) : "no defined bits set";

        private static string PromptMissing(string model, string operation, string reason) =>
            "Cannot wait for '" + operation + "' on model '" + model + "': " + reason + ".\n\n" +
            "This instrument may support SRQ, but I will not guess. Provide its statusModel: the status-byte bit " +
            "that signals completion for '" + operation + "', the named error/fail bit, the SRQ enable-mask set/clear " +
            "commands (with a {mask} placeholder, e.g. \"RQS {mask}\"/\"RQS 0\"), whether a serial poll clears RQS, and " +
            "the 'arm' command(s) that start the operation. Confirm the values, save them with instrument_db_save " +
            "(a statusModel block), then re-run.";

        private static string PromptOperation(string model, string operation, StatusModel statusModel)
        {
            string known = (statusModel.Operations != null && statusModel.Operations.Count > 0)
                ? "Known operations: " + string.Join(", ", statusModel.Operations.Keys) + "."
                : "It has no operations defined yet.";
            return "Model '" + model + "' has a statusModel but no operation named '" + operation + "'. " + known +
                   "\n\nDefine '" + operation + "' as { arm, expectBit[, restore] } and save it with instrument_db_save, then re-run.";
        }
    }
}
