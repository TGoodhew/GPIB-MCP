using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using GpibMcp.Diagnostics;
using Ivi.Visa;
using NationalInstruments.Visa;
using NationalInstruments.NI4882;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// The default <see cref="IGpibTransport"/>: NI-VISA.NET for message-based I/O (GPIB, USB-TMC,
    /// TCPIP/LXI, serial) plus NI-488.2 for native board/primary/secondary addressing. This is the
    /// only place that touches the NI driver assemblies; everything above it is backend-neutral so a
    /// different adapter (Prologix, AR488, ...) can be dropped in by implementing the interface (#22).
    ///
    /// Methods are called by <see cref="InstrumentManager"/> under its single lock, so this type does
    /// not lock internally.
    /// </summary>
    public sealed class NiVisaTransport : IGpibTransport, INativeGpib
    {
        /// <summary>1:1 byte-to-char encoding (Latin-1) for lossless string/byte conversion.</summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private readonly ResourceManager _rm = new ResourceManager();
        private readonly Dictionary<string, MessageBasedSession> _sessions =
            new Dictionary<string, MessageBasedSession>(StringComparer.OrdinalIgnoreCase);

        public TransportCapabilities Capabilities { get; } =
            new TransportCapabilities("NI-VISA", discovery: true, serialPoll: true, serviceRequest: true,
                                      deviceClear: true, returnToLocal: true, nativeAddressing: true);

        public IList<string> ListResources(string filter)
        {
            try
            {
                var found = _rm.Find(filter).ToList();
                Log.Debug("VISA Find('" + filter + "') -> " + found.Count + " resource(s)");
                return found;
            }
            catch (Exception ex)
            {
                // VISA raises VI_ERROR_RSRC_NFOUND when nothing matches; that is an expected
                // "no instruments" outcome, not a fault.
                Log.Debug("VISA Find('" + filter + "') found no resources: " + ex.Message);
                return new List<string>();
            }
        }

        public void Open(string resource, int timeoutMs)
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
        }

        private MessageBasedSession Session(string resource, int timeoutMs)
        {
            Open(resource, timeoutMs);
            return _sessions[resource];
        }

        public void Write(string resource, byte[] payload, int timeoutMs)
        {
            var session = Session(resource, timeoutMs);
            session.RawIO.Write(payload);
        }

        public TransportReadResult Read(string resource, TransportReadRequest request)
        {
            var session = Session(resource, request.TimeoutMs);

            if (request.TermChar.HasValue)
            {
                session.TerminationCharacter = (byte)request.TermChar.Value;
                session.TerminationCharacterEnabled = true;
            }
            else
            {
                session.TerminationCharacterEnabled = false;
            }

            if (request.MaxBytes > 0)
            {
                try
                {
                    return new TransportReadResult(session.RawIO.Read(request.MaxBytes), false);
                }
                catch (IOTimeoutException ex)
                {
                    // Bounded read that didn't fill before the timeout: keep the partial data
                    // NI exposes on the exception (free-running instruments / capture chunks).
                    return new TransportReadResult(ex.ActualData ?? Array.Empty<byte>(), true);
                }
            }

            // Unbounded text read: to termination/EOI as a string. (Binary block reads use the bounded
            // raw path above with a large MaxBytes - ReadString text-decodes and throws on image bytes.)
            return new TransportReadResult(Latin1.GetBytes(session.RawIO.ReadString()), false);
        }

        public int SerialPoll(string resource)
        {
            var session = Session(resource, InstrumentManager.DefaultTimeoutMs);
            int stb = (int)session.ReadStatusByte();
            Log.Debug("VISA " + resource + " serial poll -> 0x" + stb.ToString("X2"));
            return stb;
        }

        public bool WaitForSrq(string resource, int timeoutMs, out long elapsedMs)
        {
            var session = Session(resource, timeoutMs);
            var watch = Stopwatch.StartNew();
            session.EnableEvent(EventType.ServiceRequest);
            try
            {
                session.WaitOnEvent(EventType.ServiceRequest, timeoutMs);
                watch.Stop();
                elapsedMs = watch.ElapsedMilliseconds;
                Log.Debug("VISA " + resource + " SRQ asserted after " + elapsedMs + "ms");
                return true;
            }
            catch (IOTimeoutException)
            {
                watch.Stop();
                elapsedMs = watch.ElapsedMilliseconds;
                Log.Debug("VISA " + resource + " SRQ wait timed out after " + elapsedMs + "ms");
                return false;
            }
            finally
            {
                try { session.DisableEvent(EventType.ServiceRequest); }
                catch (Exception ex) { Log.Warn("WaitForSrq: DisableEvent failed: " + ex.Message); }
            }
        }

        public void Clear(string resource, int timeoutMs)
        {
            var session = Session(resource, timeoutMs);
            Log.Debug("VISA " + resource + " device clear");
            session.Clear();
        }

        public void ReturnToLocal(string resource)
        {
            MessageBasedSession session;
            if (!_sessions.TryGetValue(resource, out session)) return;
            try
            {
                var gpib = session as GpibSession;
                if (gpib != null) gpib.SendRemoteLocalCommand(GpibInstrumentRemoteLocalMode.GoToLocal);
            }
            catch (Exception ex) { Log.Warn("return-to-local failed: " + ex.Message); }
        }

        public GpibStatus DescribeError(Exception ex) => VisaErrorInfo.Describe(ex);

        public IList<string> ListOpen() => _sessions.Keys.ToList();

        public bool Close(string resource)
        {
            MessageBasedSession session;
            if (!_sessions.TryGetValue(resource, out session)) return false;
            _sessions.Remove(resource);
            DisposeSession(resource, session);
            Log.Info("Closed instrument session: " + resource);
            return true;
        }

        // ---- Native NI-488.2 addressing (board / primary / secondary) -----------
        public string NativeQuery(int board, byte primaryAddress, byte secondaryAddress, string command)
        {
            string payload = CommandText.EnsureTerminated(command);
            using (var device = new Device(board, primaryAddress, secondaryAddress))
            {
                device.Write(payload);
                return device.ReadString();
            }
        }

        public void Dispose()
        {
            foreach (var entry in _sessions) DisposeSession(entry.Key, entry.Value);
            _sessions.Clear();
            try { _rm.Dispose(); }
            catch (Exception ex) { Log.Warn("Error disposing VISA resource manager: " + ex.Message); }
        }

        private static void DisposeSession(string resource, MessageBasedSession session)
        {
            try { session.Dispose(); }
            catch (Exception ex) { Log.Warn("Error disposing session " + resource + ": " + ex.Message); }
        }
    }
}
