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
    }
}
