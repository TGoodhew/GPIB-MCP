using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Backend-neutral instrument manager: the single implementation of <see cref="IInstrumentManager"/>
    /// the tools use. It owns everything above the wire - the command history, error enrichment,
    /// per-instrument I/O behaviour (<see cref="IoSpec"/>), screen-capture orchestration, and the lock
    /// that serializes I/O on the shared bus - and delegates the actual bytes to an
    /// <see cref="IGpibTransport"/>. Swapping the transport (NI-VISA, Prologix, AR488, a test fake)
    /// changes nothing here or in the tools (issue #22).
    /// </summary>
    public sealed class InstrumentManager : IInstrumentManager, IDisposable
    {
        /// <summary>Default I/O timeout applied when the caller does not specify one.</summary>
        public const int DefaultTimeoutMs = 5000;

        /// <summary>Upper bound for a single binary image-block read (#10) - generous for any instrument screenshot.</summary>
        public const long MaxImageBlockBytes = 16L * 1024 * 1024;

        /// <summary>VISA resource filter matching any INSTR resource on any bus.</summary>
        public const string DefaultResourceFilter = "?*INSTR";

        /// <summary>NI-488.2 convention: secondary address 0 means "no secondary address".</summary>
        public const byte NoSecondaryAddress = 0;

        /// <summary>1:1 byte-to-char encoding (Latin-1) for lossless command/response conversion.</summary>
        private static readonly Encoding Latin1 = Encoding.GetEncoding("ISO-8859-1");

        private readonly IGpibTransport _transport;
        private readonly object _gate = new object();
        private readonly CommandHistory _history = new CommandHistory();
        private readonly Dictionary<string, GpibOperationException> _lastErrorByResource =
            new Dictionary<string, GpibOperationException>(StringComparer.OrdinalIgnoreCase);
        private GpibOperationException _lastError;

        public InstrumentManager(IGpibTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        /// <summary>What the active backend supports (discovery, serial poll, SRQ, native addressing, ...).</summary>
        public TransportCapabilities Capabilities => _transport.Capabilities;

        public IList<string> ListResources(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter)) filter = DefaultResourceFilter;
            lock (_gate) return _transport.ListResources(filter);
        }

        public string Query(string resource, string command, int timeoutMs) =>
            Query(resource, command, new IoSpec(timeoutMs));

        public string Query(string resource, string command, IoSpec io)
        {
            io = io ?? new IoSpec();
            lock (_gate)
            {
                string payload = CommandText.EnsureTerminated(command, io.WriteTerminator);
                try
                {
                    _transport.Open(resource, Timeout(io.TimeoutMs));
                    Log.Debug("VISA " + resource + " <- " + CommandText.ForLog(payload));
                    _history.Record(resource, CommandDirection.Sent, payload);
                    _transport.Write(resource, Latin1.GetBytes(payload), Timeout(io.TimeoutMs));
                    string response = ReadResponse(resource, io);
                    _history.Record(resource, CommandDirection.Received, response);
                    Log.Debug("VISA " + resource + " -> " + CommandText.ForLog(response));
                    return response;
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Query, resource, command, ex);
                }
            }
        }

        public byte[] QueryBlock(string resource, string command, int timeoutMs)
        {
            lock (_gate) return QueryBlockNoLock(resource, command, timeoutMs);
        }

        /// <summary>QueryBlock body without taking the gate, for callers (e.g. the record loop) already holding it.</summary>
        private byte[] QueryBlockNoLock(string resource, string command, int timeoutMs)
        {
            var io = new IoSpec(timeoutMs);
            string payload = CommandText.EnsureTerminated(command, io.WriteTerminator);
            try
            {
                _transport.Open(resource, Timeout(io.TimeoutMs));
                _history.Record(resource, CommandDirection.Sent, payload);
                _transport.Write(resource, Latin1.GetBytes(payload), Timeout(io.TimeoutMs));

                // Read the whole record as raw bytes via the bounded raw path: no termination char (data
                // may contain 0x0A and some records carry no trailing \n) and a large cap so a single read
                // returns the full record at EOI. A timed-out read returns empty (no throw).
                var req = new TransportReadRequest { TimeoutMs = Timeout(io.TimeoutMs), TermChar = null, MaxBytes = MaxImageBlockBytes };
                byte[] data = _transport.Read(resource, req).Data;
                _history.Record(resource, CommandDirection.Received, "<" + data.Length + " bytes>");
                return data;
            }
            catch (Exception ex) when (!(ex is GpibOperationException))
            {
                throw Fail(GpibOperation.Query, resource, command, ex);
            }
        }

        /// <summary>Reads one EOI-bounded record without sending a command (for the record stream, which
        /// issues its dump command once and then reads). A timed-out read returns empty (no throw).</summary>
        private byte[] ReadBlockNoLock(string resource, int timeoutMs)
        {
            try
            {
                _transport.Open(resource, Timeout(timeoutMs));
                var req = new TransportReadRequest { TimeoutMs = Timeout(timeoutMs), TermChar = null, MaxBytes = MaxImageBlockBytes };
                byte[] data = _transport.Read(resource, req).Data;
                _history.Record(resource, CommandDirection.Received, "<" + data.Length + " bytes>");
                return data;
            }
            catch (Exception ex) when (!(ex is GpibOperationException))
            {
                throw Fail(GpibOperation.Query, resource, "<read>", ex);
            }
        }

        /// <summary>
        /// Captures HP-GL from a record-output query (e.g. <c>OUTPPLOT</c> on the 8720/8753 VNAs). The
        /// dump command is sent ONCE; the instrument then streams its whole plot as many EOI-bounded HP-GL
        /// records (its IP/SC scale header first, then geometry), which we read to EOI and append until a
        /// read returns empty (the bus goes quiet) or the backstop fires. Re-sending the command per record
        /// makes the analyzer skip past the header records - confirmed against a 7440-app NI I/O trace, which
        /// writes OUTPPLOT once and then only reads. The assembled HP-GL renders through the normal plot
        /// path. Issue #55.
        /// </summary>
        public CaptureResult CaptureRecordStream(string resource, string preRoll, string command, CaptureOptions options)
        {
            options = options ?? new CaptureOptions();
            if (string.IsNullOrWhiteSpace(command)) command = "OUTPPLOT";
            int perRecordMs = options.PerReadTimeoutMs > 0 ? Math.Max(options.PerReadTimeoutMs, 2000) : 2000;

            lock (_gate)
            {
                _transport.Open(resource, perRecordMs);
                var watch = Stopwatch.StartNew();
                try
                {
                    if (!string.IsNullOrEmpty(preRoll))
                        _transport.Write(resource, Latin1.GetBytes(CommandText.EnsureTerminated(preRoll)), DefaultTimeoutMs);

                    Log.Info("Record-stream capture start: " + resource + " cmd='" + command + "'");

                    // Send the dump command ONCE, then read records until the stream is exhausted.
                    string payload = CommandText.EnsureTerminated(command);
                    _history.Record(resource, CommandDirection.Sent, payload);
                    _transport.Write(resource, Latin1.GetBytes(payload), perRecordMs);

                    var buffer = new StringBuilder();
                    int records = 0;
                    const int maxRecords = 4000; // runaway guard
                    for (int i = 0; i < maxRecords; i++)
                    {
                        byte[] rec = ReadBlockNoLock(resource, perRecordMs);
                        if (rec == null || rec.Length == 0) break;   // empty record -> plot exhausted
                        foreach (byte b in rec) buffer.Append((char)b);
                        records++;
                        if (watch.ElapsedMilliseconds >= options.OverallTimeoutMs) break;
                    }
                    var completion = records > 0 && watch.ElapsedMilliseconds < options.OverallTimeoutMs
                        ? CaptureCompletion.Inactivity : CaptureCompletion.Backstop;
                    Log.Info("Record-stream capture done: " + records + " records, " + buffer.Length +
                             " bytes, " + completion + ", " + watch.ElapsedMilliseconds + "ms");
                    return new CaptureResult(buffer.ToString(), buffer.Length, watch.ElapsedMilliseconds, completion);
                }
                finally
                {
                    _transport.ReturnToLocal(resource);
                }
            }
        }

        public void Write(string resource, string command, int timeoutMs) =>
            Write(resource, command, new IoSpec(timeoutMs));

        public void Write(string resource, string command, IoSpec io)
        {
            io = io ?? new IoSpec();
            lock (_gate)
            {
                string payload = CommandText.EnsureTerminated(command, io.WriteTerminator);
                try
                {
                    _transport.Open(resource, Timeout(io.TimeoutMs));
                    Log.Debug("VISA " + resource + " <- " + CommandText.ForLog(payload));
                    _history.Record(resource, CommandDirection.Sent, payload);
                    _transport.Write(resource, Latin1.GetBytes(payload), Timeout(io.TimeoutMs));
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Write, resource, command, ex);
                }
            }
        }

        public void WriteRaw(string resource, byte[] data, int timeoutMs)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            lock (_gate)
            {
                string note = "<raw " + data.Length + " bytes>";
                try
                {
                    _transport.Open(resource, Timeout(timeoutMs));
                    Log.Debug("VISA " + resource + " <- " + note);
                    _history.Record(resource, CommandDirection.Sent, note);
                    // Verbatim: no EnsureTerminated, no Latin1 round-trip - the exact bytes hit the wire so
                    // control bytes (HP-GL ETX 0x03, PCL NUL/ESC) survive (#70).
                    _transport.Write(resource, data, Timeout(timeoutMs));
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Write, resource, note, ex);
                }
            }
        }

        public int WriteRawStreamed(string resource, byte[] data, RawWriteOptions options)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            options = options ?? new RawWriteOptions();
            int perChunk = Timeout(options.PerChunkTimeoutMs);
            lock (_gate)
            {
                string note = "<raw stream " + data.Length + " bytes, chunk " + options.ChunkBytes + ">";
                try
                {
                    // Open once and hold the bus for the whole paced stream (one server-side operation, #77).
                    _transport.Open(resource, perChunk);
                    Log.Debug("VISA " + resource + " <- " + note);
                    _history.Record(resource, CommandDirection.Sent, note);
                    return RawStreamWriter.Stream(data, options,
                        chunk => _transport.Write(resource, chunk, perChunk),
                        ms => System.Threading.Thread.Sleep(ms));
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Write, resource, note, ex);
                }
            }
        }

        public string Read(string resource, int timeoutMs) =>
            Read(resource, new IoSpec(timeoutMs));

        public string Read(string resource, IoSpec io)
        {
            io = io ?? new IoSpec();
            lock (_gate)
            {
                try
                {
                    _transport.Open(resource, Timeout(io.TimeoutMs));
                    string response = ReadResponse(resource, io);
                    _history.Record(resource, CommandDirection.Received, response);
                    Log.Debug("VISA " + resource + " -> " + CommandText.ForLog(response));
                    return response;
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Read, resource, null, ex);
                }
            }
        }

        /// <summary>Reads a response honouring the <see cref="IoSpec"/>'s read terminator and optional bounded read.</summary>
        private string ReadResponse(string resource, IoSpec io)
        {
            var req = new TransportReadRequest
            {
                TimeoutMs = Timeout(io.TimeoutMs),
                TermChar = io.ReadTermChar,
                MaxBytes = io.MaxReadBytes.GetValueOrDefault(0)
            };
            return Latin1.GetString(_transport.Read(resource, req).Data);
        }

        public void Clear(string resource, int timeoutMs)
        {
            lock (_gate)
            {
                try
                {
                    _transport.Open(resource, Timeout(timeoutMs));
                    Log.Debug("VISA " + resource + " device clear");
                    _history.Record(resource, CommandDirection.Sent, "<device clear>");
                    _transport.Clear(resource, Timeout(timeoutMs));
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Clear, resource, "<device clear>", ex);
                }
            }
        }

        public int SerialPoll(string resource)
        {
            lock (_gate)
            {
                try { return _transport.SerialPoll(resource); }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.SerialPoll, resource, "<serial poll>", ex);
                }
            }
        }

        public SrqWaitResult WaitForSrq(string resource, int timeoutMs)
        {
            int t = Timeout(timeoutMs);
            lock (_gate)
            {
                try
                {
                    _transport.Open(resource, t);
                    bool asserted = _transport.WaitForSrq(resource, t, out long elapsed);
                    return new SrqWaitResult(asserted, elapsed);
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.WaitSrq, resource, "<wait srq>", ex);
                }
            }
        }

        /// <summary>Native NI-488.2-style query by board/primary/secondary, if the backend supports it.</summary>
        public string NativeQuery(int board, byte primaryAddress, byte secondaryAddress, string command)
        {
            string address = "GPIB" + board + "::" + primaryAddress +
                             (secondaryAddress == NoSecondaryAddress ? "" : "::" + secondaryAddress);
            lock (_gate)
            {
                var native = _transport as INativeGpib;
                try
                {
                    if (native == null)
                        throw new NotSupportedException("the " + _transport.Capabilities.Name +
                            " backend does not support native board/primary/secondary addressing");
                    _history.Record(address, CommandDirection.Sent, CommandText.EnsureTerminated(command));
                    string response = native.NativeQuery(board, primaryAddress, secondaryAddress, command);
                    _history.Record(address, CommandDirection.Received, response);
                    return response;
                }
                catch (Exception ex) when (!(ex is GpibOperationException))
                {
                    throw Fail(GpibOperation.Query, address, command, ex);
                }
            }
        }

        /// <summary>
        /// Captures an HP-GL plot via plotter emulation. Orchestration is backend-neutral (see
        /// <see cref="ScreenCapture"/>); the wire reads/writes go through the transport. Deliberately
        /// does NOT device-clear: on 8560-series analyzers a device clear also presets the instrument.
        /// </summary>
        public CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand,
                                           CaptureOptions options = null)
        {
            if (string.IsNullOrWhiteSpace(plotCommand))
                throw new ArgumentException("plotCommand must be provided", nameof(plotCommand));
            options = options ?? new CaptureOptions();

            lock (_gate)
            {
                _transport.Open(resource, options.PerReadTimeoutMs);
                var watch = Stopwatch.StartNew();
                try
                {
                    Log.Info("Capture start: " + resource + " plot='" + plotCommand + "'");
                    var channel = new TransportCaptureChannel(_transport, resource, options);
                    var result = ScreenCapture.Run(channel, preRoll, plotCommand, options,
                                                   () => watch.ElapsedMilliseconds);
                    Log.Info("Capture done: " + result.ByteCount + " bytes, " + result.Completion +
                             ", " + result.ElapsedMs + "ms");
                    return result;
                }
                finally
                {
                    // Leave the bus usable and the box in local. NOT a device clear: on HP 8560-series
                    // analyzers DCL/SDC also executes an instrument preset, wiping the user's setup.
                    _transport.ReturnToLocal(resource);
                }
            }
        }

        public IList<string> ListOpen()
        {
            lock (_gate) return _transport.ListOpen();
        }

        public bool Close(string resource)
        {
            lock (_gate) return _transport.Close(resource);
        }

        public IReadOnlyList<CommandHistoryEntry> RecentCommands(string resource, int max) =>
            _history.Snapshot(resource, max);

        public void RecordError(GpibOperationException error)
        {
            if (error == null) return;
            lock (_gate)
            {
                _lastError = error;
                if (!string.IsNullOrEmpty(error.Resource)) _lastErrorByResource[error.Resource] = error;
            }
        }

        public GpibOperationException LastError(string resource)
        {
            lock (_gate)
            {
                if (!string.IsNullOrEmpty(resource))
                {
                    GpibOperationException error;
                    return _lastErrorByResource.TryGetValue(resource, out error) ? error : null;
                }
                return _lastError;
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                try { _transport.Dispose(); }
                catch (Exception ex) { Log.Warn("Error disposing transport: " + ex.Message); }
            }
        }

        private static int Timeout(int timeoutMs) => timeoutMs <= 0 ? DefaultTimeoutMs : timeoutMs;

        /// <summary>Builds the enriched failure (decoding via the transport), records it, and returns it to throw.</summary>
        private GpibOperationException Fail(GpibOperation op, string resource, string command, Exception inner)
        {
            GpibStatus status;
            try { status = _transport.DescribeError(inner); }
            catch { status = GpibStatus.Empty; }
            var error = GpibOperationException.For(op, resource, command, inner, _history.Snapshot(resource), status);
            RecordError(error);
            return error;
        }

        /// <summary>Bridges the backend-neutral capture loop to the transport's wire I/O.</summary>
        private sealed class TransportCaptureChannel : ICaptureChannel
        {
            private readonly IGpibTransport _transport;
            private readonly string _resource;
            private readonly int _perReadTimeoutMs;
            private readonly long _chunk;

            public TransportCaptureChannel(IGpibTransport transport, string resource, CaptureOptions options)
            {
                _transport = transport;
                _resource = resource;
                _perReadTimeoutMs = options.PerReadTimeoutMs;
                _chunk = options.ReadChunkSize;
            }

            public void Send(string text) => _transport.Write(_resource, Latin1.GetBytes(text), _perReadTimeoutMs);

            public CaptureRead Read()
            {
                var r = _transport.Read(_resource, new TransportReadRequest
                {
                    TimeoutMs = _perReadTimeoutMs,
                    TermChar = '\n',          // LF EOS, per the 7470A plot path
                    MaxBytes = _chunk
                });
                return new CaptureRead(r.Data, r.TimedOut);
            }
        }
    }
}
