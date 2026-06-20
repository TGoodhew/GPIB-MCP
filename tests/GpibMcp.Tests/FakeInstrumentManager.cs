using System;
using System.Collections.Generic;
using GpibMcp.Instruments;

namespace GpibMcp.Tests
{
    /// <summary>
    /// In-memory <see cref="IInstrumentManager"/> for tests. Records calls and returns
    /// canned data so tool behaviour can be verified without any instrument attached.
    /// </summary>
    internal sealed class FakeInstrumentManager : IInstrumentManager
    {
        public readonly List<string> ResourceList = new List<string>();
        public readonly Dictionary<string, string> QueryResponses =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Writes = new List<string>();
        public readonly List<string> Reads = new List<string>();
        public readonly List<string> Clears = new List<string>();
        public readonly List<string> Closes = new List<string>();
        public readonly List<string> OpenSessions = new List<string>();

        /// <summary>Optional error injected by tests; when set, <see cref="Query"/> throws it.</summary>
        public Exception QueryError;

        private readonly CommandHistory _history = new CommandHistory();
        private GpibOperationException _lastError;

        public IList<string> ListResources(string filter) => new List<string>(ResourceList);

        public string Query(string resource, string command, int timeoutMs)
        {
            if (QueryError != null)
            {
                if (QueryError is GpibOperationException gex) RecordError(gex);
                throw QueryError;
            }
            string key = (command ?? string.Empty).Trim();
            _history.Record(resource, CommandDirection.Sent, command ?? string.Empty);
            string response = QueryResponses.TryGetValue(key, out var canned) ? canned : "RESPONSE:" + key;
            _history.Record(resource, CommandDirection.Received, response);
            return response;
        }

        public void Write(string resource, string command, int timeoutMs)
        {
            Writes.Add(resource + "|" + command);
            _history.Record(resource, CommandDirection.Sent, command ?? string.Empty);
        }

        public string Read(string resource, int timeoutMs)
        {
            Reads.Add(resource);
            string response = "READ:" + resource;
            _history.Record(resource, CommandDirection.Received, response);
            return response;
        }

        public IReadOnlyList<CommandHistoryEntry> RecentCommands(string resource, int max) =>
            _history.Snapshot(resource, max);

        public void RecordError(GpibOperationException error) { if (error != null) _lastError = error; }

        public GpibOperationException LastError(string resource) => _lastError;

        /// <summary>Canned status byte returned by <see cref="SerialPoll"/>.</summary>
        public int StatusByteValue;
        public readonly List<string> SerialPolls = new List<string>();

        /// <summary>Canned SRQ wait result.</summary>
        public SrqWaitResult SrqResult = new SrqWaitResult(true, 5);
        public readonly List<string> SrqWaits = new List<string>();

        public int SerialPoll(string resource) { SerialPolls.Add(resource); return StatusByteValue; }

        public SrqWaitResult WaitForSrq(string resource, int timeoutMs)
        {
            SrqWaits.Add(resource + "|" + timeoutMs);
            return SrqResult;
        }

        /// <summary>Canned status-byte wait result (used by instrument_wait_complete).</summary>
        public StatusByteWaitResult StatusWait = new StatusByteWaitResult(0x10, true, 100);
        public readonly List<string> StatusWaits = new List<string>();

        public StatusByteWaitResult WaitForStatusBits(string resource, int mask, int timeoutMs, int pollIntervalMs)
        {
            StatusWaits.Add(resource + "|" + mask + "|" + timeoutMs);
            return StatusWait;
        }

        public void Clear(string resource, int timeoutMs) => Clears.Add(resource);

        public IList<string> ListOpen() => new List<string>(OpenSessions);

        public bool Close(string resource)
        {
            Closes.Add(resource);
            return OpenSessions.Remove(resource);
        }

        /// <summary>Canned HP-GL returned by <see cref="CaptureScreen"/> (a small valid plot, &gt;128 bytes).</summary>
        public string CaptureHpgl =
            "IN;SP1;PU0,0;PD10000,0;PD10000,7000;PD0,7000;PD0,0;" +
            "SP2;PU500,500;PD9500,6500;PU2000,2000;PD8000,5000;" +
            "SP1;PU500,6700;LBSCREEN" + ((char)3) + ";PU0,0;SP0;";

        public readonly List<string> Captures = new List<string>();

        public CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand, CaptureOptions options)
        {
            Captures.Add(resource + "|" + preRoll + "|" + plotCommand);
            return new CaptureResult(CaptureHpgl, CaptureHpgl.Length, 0, CaptureCompletion.PenUp);
        }
    }
}
