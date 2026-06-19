using System;
using System.Collections.Generic;
using System.Linq;
using GpibMcp.Diagnostics;
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
    ///
    /// All operations are guarded by a single lock, so I/O on the shared bus is serialized.
    /// That is intentional: it is the safe default for a shared GPIB bus, and the server's
    /// request loop is single-threaded in any case.
    /// </summary>
    public sealed class VisaInstrumentManager : IDisposable
    {
        /// <summary>Default I/O timeout applied to sessions when the caller does not specify one.</summary>
        public const int DefaultTimeoutMs = 5000;

        /// <summary>VISA resource filter matching any INSTR resource on any bus.</summary>
        public const string DefaultResourceFilter = "?*INSTR";

        private readonly ResourceManager _rm = new ResourceManager();
        private readonly Dictionary<string, MessageBasedSession> _sessions =
            new Dictionary<string, MessageBasedSession>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        /// <summary>
        /// Discovers connected VISA resources. The default filter matches any INSTR resource.
        /// Returns an empty list when nothing is found (rather than throwing).
        /// </summary>
        public IList<string> ListResources(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) filter = DefaultResourceFilter;
            lock (_gate)
            {
                try
                {
                    var found = _rm.Find(filter).ToList();
                    Log.Debug("VISA Find('" + filter + "') -> " + found.Count + " resource(s)");
                    return found;
                }
                catch (Exception ex)
                {
                    // VISA raises VI_ERROR_RSRC_NFOUND when nothing matches the filter; that is
                    // an expected "no instruments" outcome rather than a fault, so we log it at
                    // Debug and return an empty list.
                    Log.Debug("VISA Find('" + filter + "') found no resources: " + ex.Message);
                    return new List<string>();
                }
            }
        }

        /// <summary>Opens (or returns the cached) session for a resource and applies the timeout.</summary>
        public MessageBasedSession Open(string resource, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(resource))
                throw new ArgumentException("resource must be provided", nameof(resource));
            if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

            lock (_gate)
            {
                MessageBasedSession session;
                if (!_sessions.TryGetValue(resource, out session))
                {
                    Log.Debug("Opening VISA session: " + resource);
                    session = (MessageBasedSession)_rm.Open(resource);
                    _sessions[resource] = session;
                    Log.Info("Opened instrument session: " + resource);
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
                string payload = CommandText.EnsureTerminated(command);
                Log.Debug("VISA " + resource + " <- " + CommandText.ForLog(payload));
                session.RawIO.Write(payload);
                string response = session.RawIO.ReadString();
                Log.Debug("VISA " + resource + " -> " + CommandText.ForLog(response));
                return response;
            }
        }

        /// <summary>Writes a command with no expected response.</summary>
        public void Write(string resource, string command, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                string payload = CommandText.EnsureTerminated(command);
                Log.Debug("VISA " + resource + " <- " + CommandText.ForLog(payload));
                session.RawIO.Write(payload);
            }
        }

        /// <summary>Reads a pending response from a previously written command.</summary>
        public string Read(string resource, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                string response = session.RawIO.ReadString();
                Log.Debug("VISA " + resource + " -> " + CommandText.ForLog(response));
                return response;
            }
        }

        /// <summary>Sends the IEEE 488.2 device clear to reset the instrument's I/O state.</summary>
        public void Clear(string resource, int timeoutMs)
        {
            lock (_gate)
            {
                var session = Open(resource, timeoutMs);
                Log.Debug("VISA " + resource + " device clear");
                session.Clear();
            }
        }

        /// <summary>Returns the resource strings of all sessions currently held open.</summary>
        public IList<string> ListOpen()
        {
            lock (_gate) { return _sessions.Keys.ToList(); }
        }

        /// <summary>
        /// Closes and forgets a held-open session. Returns false if no session was open
        /// for the resource.
        /// </summary>
        public bool Close(string resource)
        {
            lock (_gate)
            {
                MessageBasedSession session;
                if (!_sessions.TryGetValue(resource, out session)) return false;
                _sessions.Remove(resource);
                DisposeSession(resource, session);
                Log.Info("Closed instrument session: " + resource);
                return true;
            }
        }

        /// <summary>Closes every open session and the underlying VISA resource manager.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var entry in _sessions) DisposeSession(entry.Key, entry.Value);
                _sessions.Clear();

                try
                {
                    _rm.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Warn("Error disposing VISA resource manager: " + ex.Message);
                }
            }
        }

        private static void DisposeSession(string resource, MessageBasedSession session)
        {
            try
            {
                session.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Error disposing session " + resource + ": " + ex.Message);
            }
        }
    }
}
