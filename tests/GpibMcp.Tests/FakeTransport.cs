using System;
using System.Collections.Generic;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Tests
{
    /// <summary>
    /// In-memory <see cref="IGpibTransport"/> for testing the backend-neutral
    /// <see cref="InstrumentManager"/> without any driver. Records writes and serves canned reads,
    /// so the manager's encoding, history, IoSpec mapping, and capture orchestration are verifiable.
    /// </summary>
    internal sealed class FakeTransport : IGpibTransport, INativeGpib
    {
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        public readonly List<string> Writes = new List<string>();
        public readonly List<TransportReadRequest> Reads = new List<TransportReadRequest>();
        public readonly List<string> Opened = new List<string>();
        public string NextRead = "RESPONSE\n";
        public bool Disposed;

        public TransportCapabilities Capabilities { get; set; } =
            new TransportCapabilities("Fake", true, true, true, true, true, true);

        public IList<string> ListResources(string filter) => new List<string> { "GPIB0::5::INSTR" };
        public void Open(string resource, int timeoutMs) => Opened.Add(resource);
        public bool Close(string resource) => true;
        public IList<string> ListOpen() => new List<string>();

        public void Write(string resource, byte[] payload, int timeoutMs) =>
            Writes.Add(Latin1.GetString(payload));

        public TransportReadResult Read(string resource, TransportReadRequest request)
        {
            Reads.Add(request);
            return new TransportReadResult(Latin1.GetBytes(NextRead), false);
        }

        public int SerialPoll(string resource) => 0x40;
        public bool WaitForSrq(string resource, int timeoutMs, out long elapsedMs) { elapsedMs = 3; return true; }
        public void Clear(string resource, int timeoutMs) { }
        public void ReturnToLocal(string resource) { }
        public GpibStatus DescribeError(Exception ex) => GpibStatus.Empty;
        public string NativeQuery(int board, byte pad, byte sad, string command) => "NATIVE:" + command;
        public void Dispose() => Disposed = true;
    }
}
