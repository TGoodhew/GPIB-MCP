using System.Linq;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// The backend-neutral <see cref="InstrumentManager"/> over a <see cref="FakeTransport"/>:
    /// proves it drives any <see cref="IGpibTransport"/> correctly (issue #22), independent of NI-VISA.
    /// </summary>
    public class InstrumentManagerTests
    {
        [Fact]
        public void Query_WritesTerminatedCommand_AndReturnsTrimmedResponse()
        {
            var t = new FakeTransport { NextRead = "1.234\r\n" };
            var mgr = new InstrumentManager(t);

            string resp = mgr.Query("GPIB0::5::INSTR", "MEAS?", InstrumentManager.DefaultTimeoutMs);

            Assert.Equal("MEAS?\n", Assert.Single(t.Writes));   // terminator appended, sent as bytes
            Assert.Equal("1.234\r\n", resp);                     // manager returns raw; tools trim
        }

        [Fact]
        public void WriteRawStreamed_ChunksVerbatim_AndAssertsEoiOnlyOnTheLastChunk()
        {
            var t = new FakeTransport();
            var mgr = new InstrumentManager(t);
            byte[] data = System.Text.Encoding.GetEncoding("ISO-8859-1").GetBytes("ABCDEFG");   // 7 bytes

            int chunks = mgr.WriteRawStreamed("GPIB0::6::INSTR", data, new RawWriteOptions { ChunkBytes = 3 });

            Assert.Equal(3, chunks);
            Assert.Equal(new[] { "ABC", "DEF", "G" }, t.Writes);            // chunked, byte-for-byte, in order
            Assert.Equal(new[] { false, false, true }, t.WriteSendEnds);   // EOI only on the final chunk (#77)
        }

        [Fact]
        public void Query_MapsIoSpecToReadRequest()
        {
            var t = new FakeTransport();
            var mgr = new InstrumentManager(t);

            mgr.Query("GPIB0::5::INSTR", "*IDN?", new IoSpec(2000) { ReadTermChar = '\r', MaxReadBytes = 512 });

            var req = Assert.Single(t.Reads);
            Assert.Equal('\r', req.TermChar);
            Assert.Equal(512, req.MaxBytes);
            Assert.Equal(2000, req.TimeoutMs);
        }

        [Fact]
        public void QueryBlock_WritesCommand_AndReadsBinaryViaBoundedRawPath()
        {
            // Binary image bytes (incl. 0x0A, high bytes) survive the round-trip; the read must use the
            // bounded RAW path (no term char, large cap) - the unbounded text path breaks on binary (#10).
            string binary = new string(new[] { (char)0x89, (char)0x50, (char)0x0A, (char)0xFF, (char)0x42, (char)0x4D });
            var t = new FakeTransport { NextRead = binary };
            var mgr = new InstrumentManager(t);

            byte[] data = mgr.QueryBlock("GPIB0::5::INSTR", ":DISP:DATA?", 4000);

            Assert.Equal(":DISP:DATA?\n", Assert.Single(t.Writes));         // command sent (terminated)
            var req = Assert.Single(t.Reads);
            Assert.Null(req.TermChar);                                       // no termination char (binary)
            Assert.Equal(InstrumentManager.MaxImageBlockBytes, req.MaxBytes); // bounded raw read
            Assert.Equal(new byte[] { 0x89, 0x50, 0x0A, 0xFF, 0x42, 0x4D }, data);
        }

        /// <summary>A transport whose instrument has <c>Output</c> queued, read up to N bytes at a time, END at the last byte.</summary>
        private sealed class BlockTransport : FakeTransportBase
        {
            public byte[] Output = new byte[0];
            public int At;
            public int MaxPerRead = 50;
            public readonly System.Collections.Generic.List<TransportReadRequest> Requests = new System.Collections.Generic.List<TransportReadRequest>();

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                Requests.Add(request);
                int n = (int)System.Math.Min(System.Math.Min(request.MaxBytes, MaxPerRead), Output.Length - At);
                var chunk = new byte[n];
                System.Array.Copy(Output, At, chunk, 0, n);
                At += n;
                return new TransportReadResult(chunk, false, At >= Output.Length);
            }
        }

        [Fact]
        public void QueryBinary_ReadsTheWholeBlock_AsBytes_WithNoTerminationCharacter()
        {
            // #139: 0x0A in the payload must not end the read, and the block arrives over many reads.
            byte[] image = System.Linq.Enumerable.Range(0, 1000).Select(i => (byte)(i % 13 == 0 ? 0x0A : i)).ToArray();
            var t = new BlockTransport
            {
                Output = System.Text.Encoding.ASCII.GetBytes("#41000").Concat(image).Concat(new[] { (byte)'\n' }).ToArray()
            };
            var mgr = new InstrumentManager(t);

            BinaryBlockResult block = mgr.QueryBinary("GPIB0::17::INSTR", ":HCOP:SDUM:DATA? GIF", 30000, BlockFormat.Ieee4882);

            Assert.Equal(image, block.Payload);
            Assert.Equal("#41000", block.Header);
            Assert.Equal(t.Output.Length, t.At);                                  // nothing left on the instrument
            Assert.All(t.Requests, r => Assert.Null(r.TermChar));                 // never stopped on 0x0A
            Assert.Contains(mgr.RecentCommands("GPIB0::17::INSTR", 5), e => e.Text == "<1000-byte binary block #41000>");
        }

        [Fact]
        public void QueryBinary_OnAReplyThatIsNotABlock_DrainsTheRest_AndSaysWhatArrived()
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes(new string('x', 400) + "\n") };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() =>
                mgr.QueryBinary("GPIB0::17::INSTR", ":HCOP:SDUM:DATA? GIF", 30000, BlockFormat.Ieee4882));

            Assert.Contains("doesn't start with an IEEE 488.2 block header", ex.Message);
            Assert.Equal(t.Output.Length, t.At);   // drained: the next command doesn't meet these bytes
        }

        [Fact]
        public void Capabilities_ComeFromTheTransport()
        {
            var t = new FakeTransport
            {
                Capabilities = new TransportCapabilities("Prologix-ish", true, false, false, true, true, false)
            };
            var mgr = new InstrumentManager(t);

            Assert.Equal("Prologix-ish", mgr.Capabilities.Name);
            Assert.False(mgr.Capabilities.NativeAddressing);
        }

        [Fact]
        public void NativeQuery_Unsupported_Throws()
        {
            // A transport that is not INativeGpib: the manager must refuse native addressing.
            var mgr = new InstrumentManager(new NonNativeTransport());
            Assert.Throws<GpibOperationException>(() => mgr.NativeQuery(0, 5, 0, "*IDN?"));
        }

        [Fact]
        public void Capture_RunsThroughTheTransport()
        {
            // A plot ending in pen-up (SP0;) past the min-bytes threshold completes via the transport.
            var t = new PlotTransport();
            var mgr = new InstrumentManager(t);

            var result = mgr.CaptureScreen("GPIB0::18::INSTR", "SNGLS;TS;", "PLOT;",
                new CaptureOptions { MinPlotBytes = 8, PerReadTimeoutMs = 5, InactivityTimeoutMs = 5, OverallTimeoutMs = 5000 });

            Assert.Contains("SP0;", result.Hpgl);
            Assert.Equal(CaptureCompletion.PenUp, result.Completion);
            Assert.Equal(1, t.LocalCount);   // the front panel is handed back afterwards...
            Assert.Equal(0, t.ClearCount);   // ...never by a device clear, which presets an 8560 (#140)
        }

        [Fact]
        public void Capture_StillReturnsItsResult_WhenReturningToLocalFails()
        {
            var t = new PlotTransport { LocalThrows = true };
            var mgr = new InstrumentManager(t);

            var result = mgr.CaptureScreen("GPIB0::18::INSTR", "SNGLS;TS;", "PLOT;",
                new CaptureOptions { MinPlotBytes = 8, PerReadTimeoutMs = 5, InactivityTimeoutMs = 5, OverallTimeoutMs = 5000 });

            Assert.Contains("SP0;", result.Hpgl);
            Assert.Equal(1, t.LocalCount);
        }

        [Fact]
        public void ReturnToLocal_OpensTheSessionIfNeeded_AndRecordsIt()
        {
            // #140: it used to do nothing when no session was cached - e.g. right after visa_close.
            var t = new FakeTransport();
            var mgr = new InstrumentManager(t);

            string sent = mgr.ReturnToLocal("GPIB0::17::INSTR", InstrumentManager.DefaultTimeoutMs);

            Assert.Equal("GPIB Go To Local (GTL)", sent);
            Assert.Equal("GPIB0::17::INSTR", Assert.Single(t.Locals));
            Assert.Contains("GPIB0::17::INSTR", t.Opened);
            Assert.Contains(mgr.RecentCommands("GPIB0::17::INSTR", 5), e => e.Text == "<go to local>");
        }

        [Fact]
        public void ReturnToLocal_OnABackendWithoutIt_SaysSo()
        {
            var t = new FakeTransport { Capabilities = new TransportCapabilities("Bare", true, true, true, true, false, false) };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<System.NotSupportedException>(() => mgr.ReturnToLocal("GPIB0::17::INSTR", 1000));
            Assert.Contains("Bare", ex.Message);
            Assert.Empty(t.Locals);
        }

        [Fact]
        public void CaptureRecordStream_AssemblesRecords_AndStopsOnEmpty()
        {
            // OUTPPLOT-style: the dump command is sent ONCE, then each read returns one HP-GL record;
            // an empty read marks the end. Re-sending per record would skip the analyzer's header (#55).
            var t = new RecordStreamTransport(";;;;DF;IM;", "SP1;PU100,200;", "PD300,400;SP0;");
            var mgr = new InstrumentManager(t);

            var result = mgr.CaptureRecordStream("GPIB0::16::INSTR", null, "OUTPPLOT",
                new CaptureOptions { OverallTimeoutMs = 5000 });

            Assert.Equal(";;;;DF;IM;SP1;PU100,200;PD300,400;SP0;", result.Hpgl); // all records, in order
            Assert.Equal(CaptureCompletion.Inactivity, result.Completion);       // stopped on the empty record
            Assert.Equal(1, t.WriteCount);                                        // OUTPPLOT sent exactly once
        }

        private sealed class NonNativeTransport : FakeTransportBase { }

        /// <summary>Returns queued HP-GL records, one per read, then empty (the OUTPPLOT loop end).</summary>
        private sealed class RecordStreamTransport : FakeTransportBase
        {
            private readonly System.Collections.Generic.Queue<string> _records;
            public int WriteCount;
            public RecordStreamTransport(params string[] records) =>
                _records = new System.Collections.Generic.Queue<string>(records);
            public override void Write(string resource, byte[] payload, int timeoutMs) => WriteCount++;
            public override TransportReadResult Read(string resource, TransportReadRequest request) =>
                _records.Count == 0
                    ? new TransportReadResult(System.Array.Empty<byte>(), true)
                    : new TransportReadResult(System.Text.Encoding.ASCII.GetBytes(_records.Dequeue()), false);
        }

        /// <summary>A transport that returns a small pen-up plot once, then times out (idle).</summary>
        private sealed class PlotTransport : FakeTransportBase
        {
            private bool _sent;
            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                if (_sent) return new TransportReadResult(System.Array.Empty<byte>(), true);
                _sent = true;
                return new TransportReadResult(System.Text.Encoding.ASCII.GetBytes("IN;SP1;PU;PD100,100;SP0;"), true);
            }
        }

        // Minimal IGpibTransport (NOT INativeGpib) for the cases above.
        private abstract class FakeTransportBase : IGpibTransport
        {
            public TransportCapabilities Capabilities { get; } =
                new TransportCapabilities("Bare", true, true, true, true, true, false);
            public System.Collections.Generic.IList<string> ListResources(string filter) => new System.Collections.Generic.List<string>();
            public void Open(string resource, int timeoutMs) { }
            public bool Close(string resource) => true;
            public System.Collections.Generic.IList<string> ListOpen() => new System.Collections.Generic.List<string>();
            public virtual void Write(string resource, byte[] payload, int timeoutMs) { }
            public virtual void Write(string resource, byte[] payload, int timeoutMs, bool sendEnd) =>
                Write(resource, payload, timeoutMs);
            public virtual TransportReadResult Read(string resource, TransportReadRequest request) =>
                new TransportReadResult(System.Array.Empty<byte>(), true);
            public int SerialPoll(string resource) => 0;
            public bool WaitForSrq(string resource, int timeoutMs, out long elapsedMs) { elapsedMs = 0; return false; }
            public int ClearCount, LocalCount;
            public bool LocalThrows;
            public void Clear(string resource, int timeoutMs) => ClearCount++;
            public string ReturnToLocal(string resource, int timeoutMs)
            {
                LocalCount++;
                if (LocalThrows) throw new System.InvalidOperationException("bus error");
                return "GPIB Go To Local (GTL)";
            }
            public GpibStatus DescribeError(System.Exception ex) => GpibStatus.Empty;
            public void Dispose() { }
        }
    }
}
