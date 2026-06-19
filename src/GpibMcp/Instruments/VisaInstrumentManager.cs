using System;
using System.Collections.Generic;
using System.Linq;
using Ivi.Visa;
using NationalInstruments.Visa;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Owns a single NI VISA.NET <see cref="ResourceManager"/> and a cache of open
    /// message-based sessions keyed by resource string. Sessions are reused across calls
    /// so an instrument stays addressed (and configured) between tool invocations.
    ///
    /// VISA covers every common bus - GPIB, USB-TMC, TCPIP/LXI, and serial - through the
    /// same message-based API, which is why it is the primary path for this server.
    /// </summary>
    public sealed class VisaInstrumentManager : IDisposable
    {
        private readonly ResourceManager _rm = new ResourceManager();
        private readonly Dictionary<string, MessageBasedSession> _sessions =
            new Dictionary<string, MessageBasedSession>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public const int DefaultTimeoutMs = 5000;

        /// <summary>
        /// Discovers connected VISA resources. The default filter matches any INSTR resource.
        /// Returns an empty list when nothing is found (rather than throwing).
        /// </summary>
        public IList<string> ListResources(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) filter = "?*INSTR";
            lock (_gate)
            {
                try
                {
                    return _rm.Find(filter).ToList();
                }
                catch (Exception)
                {
                    // VISA throws VI_ERROR_RSRC_NFOUND when there are no matches.
                    return new List<string>();
                }
            }
        }

        /// <summary>Opens (or returns the cached) session for a resource and applies the timeout.</summary>
        public MessageBasedSession Open(string resource, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("resource must be provided");
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            lock (_gate)
            {
                MessageBasedSession session;
                if (!_sessions.TryGetValue(resource, out session))
                {
                    session = (MessageBasedSession)_rm.Open(resource);
                    _sessions[resource] = session;
                }
                session.TimeoutMilliseconds = timeoutMs;
                return session;
            }
        }

        /// <summary>Writes a command and reads the instrument's response (e.g. SCPI "*IDN?").</summary>
        public string Query(string resource, string command, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                session.RawIO.Write(EnsureTermination(command));
                return session.RawIO.ReadString();
            }
        }

        /// <summary>Writes a command with no expected response.</summary>
        public void Write(string resource, string command, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                session.RawIO.Write(EnsureTermination(command));
            }
        }

        /// <summary>Reads a pending response from a previously written command.</summary>
        public string Read(string resource, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                return session.RawIO.ReadString();
            }
        }

        /// <summary>Sends the IEEE 488.2 device clear to reset the instrument's I/O state.</summary>
        public void Clear(string resource, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                session.Clear();
            }
        }

        public IList<string> ListOpen()
        {
            lock (_gate) { return _sessions.Keys.ToList(); }
        }

        public bool Close(string resource)
        {
            lock (_gate)
            {
                MessageBasedSession session;
                if (!_sessions.TryGetValue(resource, out session)) return false;
                _sessions.Remove(resource);
                try { session.Dispose(); } catch { /* best effort */ }
                return true;
            }
        }

        /// <summary>Appends a newline terminator if the caller did not supply one.</summary>
        private static string EnsureTermination(string command)
        {
            if (command == null) command = string.Empty;
            return command.EndsWith("\n") ? command : command + "\n";
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var session in _sessions.Values)
                {
                    try { session.Dispose(); } catch { /* best effort */ }
                }
                _sessions.Clear();
                try { _rm.Dispose(); } catch { /* best effort */ }
            }
        }
    }
}
