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

        public IList<string> ListResources(string filter) => new List<string>(ResourceList);

        public string Query(string resource, string command, int timeoutMs)
        {
            string key = (command ?? string.Empty).Trim();
            if (QueryResponses.TryGetValue(key, out var response)) return response;
            return "RESPONSE:" + key;
        }

        public void Write(string resource, string command, int timeoutMs) =>
            Writes.Add(resource + "|" + command);

        public string Read(string resource, int timeoutMs)
        {
            Reads.Add(resource);
            return "READ:" + resource;
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
