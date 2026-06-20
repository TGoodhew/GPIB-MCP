using System.Collections.Generic;

namespace GpibMcp.Instruments
{
    /// <summary>Outcome of an SRQ wait: whether the service request asserted, and how long it took.</summary>
    public sealed class SrqWaitResult
    {
        public bool Asserted { get; }
        public long ElapsedMs { get; }
        public SrqWaitResult(bool asserted, long elapsedMs) { Asserted = asserted; ElapsedMs = elapsedMs; }
    }

    /// <summary>
    /// Outcome of waiting for status-byte bits: the last status byte read, whether any of the
    /// requested mask bits were seen before the timeout, and the elapsed time.
    /// </summary>
    public sealed class StatusByteWaitResult
    {
        public int StatusByte { get; }
        public bool Matched { get; }
        public long ElapsedMs { get; }
        public StatusByteWaitResult(int statusByte, bool matched, long elapsedMs)
        {
            StatusByte = statusByte;
            Matched = matched;
            ElapsedMs = elapsedMs;
        }
    }

    /// <summary>
    /// Abstraction over the instrument I/O layer consumed by the MCP tools.
    /// <see cref="VisaInstrumentManager"/> implements it for real hardware; tests can
    /// substitute a fake so tool behaviour is verifiable without instruments attached.
    /// </summary>
    public interface IInstrumentManager
    {
        /// <summary>Discovers connected VISA resources matching <paramref name="filter"/>.</summary>
        IList<string> ListResources(string filter);

        /// <summary>Writes a command and reads the instrument's response.</summary>
        string Query(string resource, string command, int timeoutMs);

        /// <summary>Writes a command with no expected response.</summary>
        void Write(string resource, string command, int timeoutMs);

        /// <summary>Reads a pending response from a previously written command.</summary>
        string Read(string resource, int timeoutMs);

        /// <summary>Sends an IEEE 488.2 device clear.</summary>
        void Clear(string resource, int timeoutMs);

        /// <summary>Returns the resource strings of all sessions currently held open.</summary>
        IList<string> ListOpen();

        /// <summary>Closes a held-open session; returns false if none was open for the resource.</summary>
        bool Close(string resource);

        /// <summary>
        /// Returns up to <paramref name="max"/> of the most recent commands sent to / responses
        /// received from a resource (oldest first) - the chain leading up to now or to an error.
        /// </summary>
        IReadOnlyList<CommandHistoryEntry> RecentCommands(string resource, int max);

        /// <summary>
        /// The most recent GPIB/VISA failure for <paramref name="resource"/>, or - when it is null/empty -
        /// the most recent failure on any resource. Returns null if none has occurred this session.
        /// </summary>
        GpibOperationException LastError(string resource);

        /// <summary>Records a failure so it is retrievable via <see cref="LastError"/> (e.g. from the NI-488.2 path).</summary>
        void RecordError(GpibOperationException error);

        /// <summary>Serial-polls the instrument and returns its status byte (0-255).</summary>
        int SerialPoll(string resource);

        /// <summary>
        /// Blocks until the instrument asserts SRQ or <paramref name="timeoutMs"/> elapses (the backstop).
        /// Pure mechanism - does not serial-poll. Always tears down the event registration.
        /// </summary>
        SrqWaitResult WaitForSrq(string resource, int timeoutMs);

        /// <summary>
        /// Serial-polls the status byte repeatedly until any bit in <paramref name="mask"/> is set or
        /// the timeout elapses. The latched status byte is the reliable completion read (it stays set
        /// until read), avoiding the event/poll race of SRQ + a separate poll.
        /// </summary>
        StatusByteWaitResult WaitForStatusBits(string resource, int mask, int timeoutMs, int pollIntervalMs);

        /// <summary>
        /// Captures an HP-GL plot from the instrument (plotter emulation): sends pre-roll + plot
        /// command, answers the handshake, and returns the raw HP-GL. Leaves the bus usable.
        /// </summary>
        CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand, CaptureOptions options);
    }
}
