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
    /// stale status, arms the SRQ mask, starts the operation, and confirms completion by polling the
    /// LATCHED status byte (reliable - bits stay set until read), then clears the mask and restores.
    /// </summary>
    public static class CompletionWaiter
    {
        public const int DefaultPollIntervalMs = 100;
        public const int DefaultTimeoutMs = 30000;

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

            int? errorBit = model.BitValue(model.ErrorBit);   // generic, model-named error/fail bit
            int mask = expect.Value | (errorBit ?? 0);
            long start = nowMs();
            log("run '" + operationName + "' on " + modelName + ": expect '" + op.ExpectBit + "'=0x" +
                expect.Value.ToString("X2") + ", errorBit '" + (model.ErrorBit ?? "-") + "'=0x" + (errorBit ?? 0).ToString("X2") +
                ", mask=" + mask + " (0x" + mask.ToString("X2") + "), timeout=" + timeoutMs + "ms");

            // Pre-clear any STALE latched status (e.g. an END OF SWEEP left set by a prior sweep) so the
            // just-armed mask cannot fire on old state and we wait for a FRESH completion.
            int stale = channel.SerialPoll();
            log("pre-clear serial poll -> 0x" + stale.ToString("X2"));

            string setCmd = model.EnableMask.SetCommand.Replace("{mask}", mask.ToString(CultureInfo.InvariantCulture));
            channel.Send(setCmd); log("send (arm mask): " + setCmd);
            if (!string.IsNullOrEmpty(op.Arm)) { channel.Send(op.Arm); log("send (start op): " + op.Arm); }

            int stb = 0;
            long elapsed = 0;
            while (true)
            {
                stb = channel.SerialPoll();
                elapsed = nowMs() - start;
                log("poll @ " + elapsed + "ms -> 0x" + stb.ToString("X2"));
                if ((stb & mask) != 0) break;          // completion or error bit appeared
                if (elapsed >= timeoutMs) break;       // backstop
                sleep(pollIntervalMs);
            }

            SafeSend(channel, model.EnableMask.ClearCommand, "clear mask", log);
            SafeSend(channel, op.Restore, "restore", log);

            bool done = (stb & expect.Value) == expect.Value;
            bool err = errorBit.HasValue && (stb & errorBit.Value) == errorBit.Value;
            IReadOnlyList<string> bits = model.SetBitNames(stb);
            string detail = "status byte " + stb + " (0x" + stb.ToString("X2") + ") [" + Describe(bits) + "]";

            CompletionResult result;
            if (err)
                result = new CompletionResult(CompletionOutcome.InstrumentError,
                    "Instrument signalled an ERROR during '" + operationName + "' after " + elapsed + " ms - " + detail +
                    (done ? " (the expected completion bit is also set)" : "") + ".", stb, elapsed, bits);
            else if (done)
                result = new CompletionResult(CompletionOutcome.Completed,
                    "Completed '" + operationName + "' after " + elapsed + " ms - " + detail + ".", stb, elapsed, bits);
            else
                result = new CompletionResult(CompletionOutcome.TimedOut,
                    "Timed out after " + timeoutMs + " ms waiting for '" + operationName + "' (expected bit '" +
                    op.ExpectBit + "' not set) - " + detail + ". Mask cleared; bus left usable.", stb, elapsed, bits);

            log("=> " + result.Outcome + ": " + result.Message);
            return result;
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
