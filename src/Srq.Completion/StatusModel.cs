using System.Collections.Generic;
using System.Linq;

namespace Srq.Completion
{
    /// <summary>
    /// How an instrument signals operation completion via the GPIB status byte / SRQ. Drives the
    /// data-driven <see cref="CompletionWaiter"/> so SRQ masks are never hardcoded. Loaded from the
    /// instrument database JSON (Json.NET matches the camelCase fields case-insensitively, so this
    /// type carries no serialization attributes and the library stays dependency-free).
    /// Optional; absent means "unknown" and <c>SrqSupported == false</c> means there is no usable SRQ.
    ///
    /// This type is the ONLY place instrument-specific completion knowledge lives - the waiter contains
    /// no per-device logic. Two completion strategies, chosen by whether <see cref="RequestServiceBit"/>
    /// is set:
    ///   * <b>SRQ-edge</b> (RequestServiceBit set): poll the GPIB request-service bit (0x40) as the
    ///     completion signal, after first waiting for the operation to go busy. Most robust; use when
    ///     the instrument reliably asserts SRQ on the armed condition (e.g. the 8563E).
    ///   * <b>direct-bit</b> (RequestServiceBit absent): poll the operation's <c>expectBit</c> (or the
    ///     error bit) directly. Use when request-service is unavailable or unreliable - e.g. the 3325,
    ///     whose require-service bit only asserts in the unit's physical "Enhancements" mode.
    /// To add a new SRQ instrument, supply this block in its database JSON; no code changes are needed.
    /// </summary>
    public sealed class StatusModel
    {
        /// <summary>Whether this instrument supports SRQ-based completion at all.</summary>
        public bool SrqSupported { get; set; } = true;

        public SerialPollSpec SerialPoll { get; set; }
        public EnableMaskSpec EnableMask { get; set; }
        public DoneSupportSpec DoneSupport { get; set; }

        /// <summary>
        /// Name (in <see cref="Bits"/>) of the bit that signals an operation FAILURE, so the waiter
        /// includes it in the mask and a failure interrupts the wait. Instrument-specific - e.g.
        /// "error" on the 8560 series, "fail" on the 3325. Optional.
        /// </summary>
        public string ErrorBit { get; set; }

        /// <summary>
        /// Name (in <see cref="Bits"/>) of the serial-poll REQUEST-SERVICE bit (the GPIB RQS bit,
        /// 0x40). When set, the waiter uses the robust "SRQ-edge" completion flow: it does NOT arm
        /// this bit, but treats its assertion as the completion signal, and first waits for the
        /// operation to go BUSY (the expect bit clears) so a condition that is already true at
        /// arm-time cannot be mistaken for completion. Hardware-confirmed on the 8563E, where the
        /// RQS-mask and the read-back status byte share the Table 7-266 layout (request-service=0x40
        /// is set on every SRQ, NOT an error). Absent =&gt; legacy direct-bit flow (poll the expect bit).
        /// </summary>
        public string RequestServiceBit { get; set; }

        /// <summary>
        /// SRQ-edge only: how long (ms) to wait for the operation to go BUSY (the expect bit to clear)
        /// before proceeding to wait for completion. Override per model when an instrument is slow to
        /// start an operation; null uses <see cref="CompletionWaiter.BusyConfirmMs"/>. Always capped at
        /// the overall timeout.
        /// </summary>
        public int? BusyConfirmMs { get; set; }

        /// <summary>Named status-byte bits and their decimal weights (e.g. "endOfSweep" -&gt; 16).</summary>
        public Dictionary<string, int> Bits { get; set; }

        /// <summary>Named operations the waiter can run (e.g. "sweepComplete" -&gt; { arm, expectBit }).</summary>
        public Dictionary<string, StatusOperation> Operations { get; set; }

        /// <summary>The decimal weight of a named bit, or null if unknown.</summary>
        public int? BitValue(string name)
        {
            int value;
            return (name != null && Bits != null && Bits.TryGetValue(name, out value)) ? value : (int?)null;
        }

        /// <summary>Names of the defined bits set in <paramref name="statusByte"/>, highest weight first.</summary>
        public IReadOnlyList<string> SetBitNames(int statusByte)
        {
            var list = new List<string>();
            if (Bits == null) return list;
            foreach (var kv in Bits.OrderByDescending(k => k.Value))
                if (kv.Value != 0 && (statusByte & kv.Value) == kv.Value)
                    list.Add(kv.Key + " (0x" + kv.Value.ToString("X2") + ")");
            return list;
        }
    }

    /// <summary>How a serial poll behaves for this instrument.</summary>
    public sealed class SerialPollSpec
    {
        /// <summary>Whether reading the status byte (serial poll) clears the RQS condition.</summary>
        public bool ClearsRqs { get; set; }
    }

    /// <summary>Commands that set/clear the SRQ enable mask, with a <c>{mask}</c> placeholder.</summary>
    public sealed class EnableMaskSpec
    {
        /// <summary>Command to enable a mask, e.g. "RQS {mask}" (8560) or "ESTB {mask}" (3325).</summary>
        public string SetCommand { get; set; }

        /// <summary>Command to clear the mask, e.g. "RQS 0".</summary>
        public string ClearCommand { get; set; }

        /// <summary>Format of the {mask} substitution: "decimal" (default) or "alpha".</summary>
        public string MaskFormat { get; set; }
    }

    /// <summary>Whether the instrument has an operation-complete ("DONE") mechanism.</summary>
    public sealed class DoneSupportSpec
    {
        public bool Supported { get; set; }
        /// <summary>The mnemonic that requests an operation-complete signal, e.g. "DONE".</summary>
        public string Mnemonic { get; set; }
    }

    /// <summary>A named completion operation: how to arm it, which status bit confirms it, optional restore.</summary>
    public sealed class StatusOperation
    {
        /// <summary>Commands that start the operation (the waiter sends the enable mask first), e.g. "SNGLS;TS;".</summary>
        public string Arm { get; set; }

        /// <summary>Name (in <see cref="StatusModel.Bits"/>) of the bit that signals completion.</summary>
        public string ExpectBit { get; set; }

        /// <summary>Optional command sent after completion to restore prior state, e.g. "CONTS;".</summary>
        public string Restore { get; set; }
    }
}
