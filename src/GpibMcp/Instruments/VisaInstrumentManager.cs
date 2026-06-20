using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using GpibMcp.Diagnostics;
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
    ///
    /// All operations are guarded by a single lock, so I/O on the shared bus is serialized.
    /// That is intentional: it is the safe default for a shared GPIB bus, and the server's
    /// request loop is single-threaded in any case.
    /// </summary>
    public sealed class VisaInstrumentManager : IInstrumentManager, IDisposable
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

        /// <summary>
        /// Captures an HP-GL plot from an instrument via plotter emulation: sends the pre-roll and
        /// plot command, answers the OS handshake, and collects the HP-GL the instrument streams.
        /// Always device-clears and returns the instrument to local afterward, leaving the bus usable.
        /// </summary>
        public CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand,
                                           CaptureOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(plotCommand))
                throw new ArgumentException("plotCommand must be provided", nameof(plotCommand));
            options = options ?? new CaptureOptions();

            lock (_gate)
            {
                var session = Open(resource, options.PerReadTimeoutMs);

                byte origTermChar = session.TerminationCharacter;
                bool origTermEnabled = session.TerminationCharacterEnabled;
                int origTimeout = session.TimeoutMilliseconds;
                var watch = Stopwatch.StartNew();

                try
                {
                    session.TerminationCharacter = (byte)'\n';   // LF EOS, per the 7470A plot path
                    session.TerminationCharacterEnabled = true;

                    Log.Info("Capture start: " + resource + " plot='" + plotCommand + "'");
                    var channel = new VisaCaptureChannel(session, options);
                    var result = ScreenCapture.Run(channel, preRoll, plotCommand, options,
                                                   () => watch.ElapsedMilliseconds);
                    Log.Info("Capture done: " + result.ByteCount + " bytes, " + result.Completion +
                             ", " + result.ElapsedMs + "ms");
                    return result;
                }
                finally
                {
                    // Restore session state and leave the bus in a usable, local state - always.
                    try
                    {
                        session.TerminationCharacter = origTermChar;
                        session.TerminationCharacterEnabled = origTermEnabled;
                        session.TimeoutMilliseconds = origTimeout;
                    }
                    catch (Exception ex) { Log.Warn("Capture: restoring session state failed: " + ex.Message); }

                    try { session.Clear(); }
                    catch (Exception ex) { Log.Warn("Capture: device clear failed: " + ex.Message); }

                    ReturnToLocal(session);
                }
            }
        }

        private static void ReturnToLocal(MessageBasedSession session)
        {
            try
            {
                var gpib = session as GpibSession;
                if (gpib != null) gpib.SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
            }
            catch (Exception ex) { Log.Warn("Capture: return-to-local failed: " + ex.Message); }
        }

        /// <summary>NI-VISA implementation of <see cref="ICaptureChannel"/> over a message-based session.</summary>
        private sealed class VisaCaptureChannel : ICaptureChannel
        {
            private readonly MessageBasedSession _session;
            private readonly int _perReadTimeoutMs;
            private readonly long _chunk;

            public VisaCaptureChannel(MessageBasedSession session, CaptureOptions options)
            {
                _session = session;
                _perReadTimeoutMs = options.PerReadTimeoutMs;
                _chunk = options.ReadChunkSize;
            }

            public void Send(string text) => _session.RawIO.Write(text);

            public CaptureRead Read()
            {
                _session.TimeoutMilliseconds = _perReadTimeoutMs;
                try
                {
                    return new CaptureRead(_session.RawIO.Read(_chunk), false);
                }
                catch (IOTimeoutException ex)
                {
                    // A per-read timeout is the instrument pausing/finishing; keep the partial data
                    // it had already sent (NI-VISA exposes it on the exception).
                    return new CaptureRead(ex.ActualData ?? Array.Empty<byte>(), true);
                }
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
