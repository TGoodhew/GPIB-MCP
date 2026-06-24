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
    /// Abstraction over the instrument I/O layer consumed by the MCP tools.
    /// <see cref="InstrumentManager"/> implements it for real hardware (over an <see cref="IGpibTransport"/>); tests can
    /// substitute a fake so tool behaviour is verifiable without instruments attached.
    /// </summary>
    public interface IInstrumentManager
    {
        /// <summary>What the active GPIB backend supports (discovery, serial poll, SRQ, native addressing, ...).</summary>
        TransportCapabilities Capabilities { get; }

        /// <summary>Discovers connected VISA resources matching <paramref name="filter"/>.</summary>
        IList<string> ListResources(string filter);

        /// <summary>Writes a command and reads the instrument's response.</summary>
        string Query(string resource, string command, int timeoutMs);

        /// <summary>
        /// Writes a query and reads the full binary response to EOI (no termination-character handling),
        /// returning the raw bytes - for IEEE 488.2 arbitrary-block responses such as a SCPI screen
        /// image (<c>:DISP:DATA?</c>). The caller strips the <c>#&lt;n&gt;&lt;len&gt;</c> header (#10).
        /// </summary>
        byte[] QueryBlock(string resource, string command, int timeoutMs);

        /// <summary>
        /// Writes a command and reads the response using the given per-instrument I/O behaviour
        /// (terminators and an optional bounded read), e.g. for a free-running instrument (issue #35).
        /// </summary>
        string Query(string resource, string command, IoSpec io);

        /// <summary>Writes a command with no expected response.</summary>
        void Write(string resource, string command, int timeoutMs);

        /// <summary>Writes a command (no response) using the given per-instrument write terminator.</summary>
        void Write(string resource, string command, IoSpec io);

        /// <summary>Writes raw bytes VERBATIM - no terminator added, no encoding/normalization - for passing
        /// control-byte-bearing payloads (e.g. HP-GL with ETX label terminators, binary PCL) to an instrument
        /// intact (#70). The bytes go straight to the transport's raw write.</summary>
        void WriteRaw(string resource, byte[] data, int timeoutMs);

        /// <summary>Reads a pending response from a previously written command.</summary>
        string Read(string resource, int timeoutMs);

        /// <summary>Reads a pending response using the given per-instrument read termination / bounded read.</summary>
        string Read(string resource, IoSpec io);

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
        /// Native query by board/primary/secondary (NI-488.2 style), for backends whose
        /// <see cref="Capabilities"/> report <see cref="TransportCapabilities.NativeAddressing"/>.
        /// Throws if the active backend does not support it.
        /// </summary>
        string NativeQuery(int board, byte primaryAddress, byte secondaryAddress, string command);

        /// <summary>
        /// Blocks until the instrument asserts SRQ or <paramref name="timeoutMs"/> elapses (the backstop).
        /// Pure mechanism - does not serial-poll. Always tears down the event registration.
        /// </summary>
        SrqWaitResult WaitForSrq(string resource, int timeoutMs);

        /// <summary>
        /// Captures an HP-GL plot from the instrument (plotter emulation): sends pre-roll + plot
        /// command, answers the handshake, and returns the raw HP-GL. Leaves the bus usable.
        /// </summary>
        CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand, CaptureOptions options);

        /// <summary>
        /// Captures HP-GL by looping a record-output query (e.g. <c>OUTPPLOT</c> on the 8720/8753 VNAs),
        /// reading each record to EOI and assembling them until an empty record signals the end (#55).
        /// </summary>
        CaptureResult CaptureRecordStream(string resource, string preRoll, string command, CaptureOptions options);
    }
}
