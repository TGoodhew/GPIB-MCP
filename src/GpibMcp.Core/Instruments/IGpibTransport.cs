using System;
using System.Collections.Generic;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// A decoded backend status for a failed operation: a short name, a plain-English meaning, and
    /// the raw numeric code when the backend has one (e.g. a VISA status). Empty when the backend
    /// could not decode the failure (the raw exception message is then used instead).
    /// </summary>
    public struct GpibStatus
    {
        public string Name { get; }
        public string Meaning { get; }
        public int? Code { get; }
        public bool HasName => !string.IsNullOrEmpty(Name);
        public GpibStatus(string name, string meaning, int? code = null) { Name = name; Meaning = meaning; Code = code; }
        public static readonly GpibStatus Empty = new GpibStatus(null, null);
    }

    /// <summary>
    /// What a GPIB backend can actually do, so higher-level tools degrade or refuse cleanly rather
    /// than guessing. Not every adapter supports discovery, serial poll, SRQ, or native addressing.
    /// </summary>
    public sealed class TransportCapabilities
    {
        /// <summary>Human-readable backend name (e.g. "NI-VISA").</summary>
        public string Name { get; }
        /// <summary>Can enumerate connected resources (<see cref="IGpibTransport.ListResources"/>).</summary>
        public bool Discovery { get; }
        /// <summary>Supports serial poll -> status byte.</summary>
        public bool SerialPoll { get; }
        /// <summary>Supports waiting on a GPIB service request (SRQ).</summary>
        public bool ServiceRequest { get; }
        /// <summary>Supports the IEEE 488.2 device clear.</summary>
        public bool DeviceClear { get; }
        /// <summary>Supports returning an instrument to local control.</summary>
        public bool ReturnToLocal { get; }
        /// <summary>Supports native board/primary/secondary addressing (see <see cref="INativeGpib"/>).</summary>
        public bool NativeAddressing { get; }

        public TransportCapabilities(string name, bool discovery, bool serialPoll, bool serviceRequest,
                                     bool deviceClear, bool returnToLocal, bool nativeAddressing)
        {
            Name = name;
            Discovery = discovery;
            SerialPoll = serialPoll;
            ServiceRequest = serviceRequest;
            DeviceClear = deviceClear;
            ReturnToLocal = returnToLocal;
            NativeAddressing = nativeAddressing;
        }
    }

    /// <summary>A single read request at the wire level.</summary>
    public sealed class TransportReadRequest
    {
        /// <summary>I/O timeout in milliseconds.</summary>
        public int TimeoutMs { get; set; }
        /// <summary>Read termination character; null disables the termination character (read to EOI / max bytes).</summary>
        public char? TermChar { get; set; }
        /// <summary>
        /// Maximum bytes to read. 0 means read to termination/EOI (a timeout is an error and throws).
        /// When &gt; 0, the read returns once that many bytes arrive, and returns the partial data with
        /// <see cref="TransportReadResult.TimedOut"/> set if the instrument falls silent first (no throw).
        /// </summary>
        public long MaxBytes { get; set; }
    }

    /// <summary>The bytes read, and whether a bounded read hit its per-read timeout (partial data).</summary>
    public struct TransportReadResult
    {
        public byte[] Data { get; }
        public bool TimedOut { get; }
        public TransportReadResult(byte[] data, bool timedOut) { Data = data ?? Array.Empty<byte>(); TimedOut = timedOut; }
    }

    /// <summary>
    /// The wire-level GPIB transport that an instrument backend implements. The backend-neutral
    /// <see cref="InstrumentManager"/> sits above it and owns sessions semantics it needs (history,
    /// errors, capture orchestration, I/O serialization); the transport just moves bytes and reports
    /// what it can do. Adding a new adapter (Prologix, AR488, ...) means implementing this interface -
    /// no changes to the tools, the instrument database, or the MCP plumbing (issue #22).
    ///
    /// All methods are invoked by the manager under a single lock, so implementations need not be
    /// internally thread-safe.
    /// </summary>
    public interface IGpibTransport : IDisposable
    {
        /// <summary>What this backend supports.</summary>
        TransportCapabilities Capabilities { get; }

        /// <summary>Discovers connected resources matching <paramref name="filter"/> (empty list if unsupported/none).</summary>
        IList<string> ListResources(string filter);

        /// <summary>Opens (or returns the cached) connection to a resource and applies the timeout.</summary>
        void Open(string resource, int timeoutMs);

        /// <summary>Closes a held-open connection; returns false if none was open for the resource.</summary>
        bool Close(string resource);

        /// <summary>Resource strings of all connections currently held open.</summary>
        IList<string> ListOpen();

        /// <summary>Writes raw bytes to a resource (asserts EOI on the last byte).</summary>
        void Write(string resource, byte[] payload, int timeoutMs);

        /// <summary>Writes raw bytes, controlling whether EOI is asserted on the final byte. For a chunked
        /// stream (#77), intermediate chunks pass <paramref name="sendEnd"/>=false so a mid-message EOI doesn't
        /// fragment the stream (which would make a plotter mis-parse a coordinate split across a chunk
        /// boundary); only the last chunk asserts EOI.</summary>
        void Write(string resource, byte[] payload, int timeoutMs, bool sendEnd);

        /// <summary>Reads from a resource per the request (see <see cref="TransportReadRequest"/>).</summary>
        TransportReadResult Read(string resource, TransportReadRequest request);

        /// <summary>Serial-polls the instrument and returns its status byte (0-255).</summary>
        int SerialPoll(string resource);

        /// <summary>Waits for SRQ; returns true if asserted before <paramref name="timeoutMs"/>, and the elapsed time.</summary>
        bool WaitForSrq(string resource, int timeoutMs, out long elapsedMs);

        /// <summary>Sends the IEEE 488.2 device clear.</summary>
        void Clear(string resource, int timeoutMs);

        /// <summary>Returns the instrument to local control (no-op if unsupported).</summary>
        void ReturnToLocal(string resource);

        /// <summary>Decodes a backend exception to a <see cref="GpibStatus"/> (Empty if it cannot).</summary>
        GpibStatus DescribeError(Exception ex);
    }

    /// <summary>
    /// Optional capability for backends that can address a device by board/primary/secondary directly
    /// (NI-488.2 style), bypassing a resource string. Backends that support it advertise
    /// <see cref="TransportCapabilities.NativeAddressing"/> and implement this on the same object.
    /// </summary>
    public interface INativeGpib
    {
        /// <summary>Opens a transient device handle at board/primary/secondary, writes, and reads the response.</summary>
        string NativeQuery(int board, byte primaryAddress, byte secondaryAddress, string command);
    }
}
