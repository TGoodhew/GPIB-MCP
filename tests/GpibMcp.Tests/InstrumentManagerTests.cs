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
            /// <summary>Never asserts END: once the output runs out, reads time out.</summary>
            public bool FallsSilent;
            public readonly System.Collections.Generic.List<TransportReadRequest> Requests = new System.Collections.Generic.List<TransportReadRequest>();

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                Requests.Add(request);
                int n = (int)System.Math.Min(System.Math.Min(request.MaxBytes, MaxPerRead), Output.Length - At);
                var chunk = new byte[n];
                System.Array.Copy(Output, At, chunk, 0, n);
                At += n;
                if (FallsSilent) return new TransportReadResult(chunk, n == 0, false);
                return new TransportReadResult(chunk, false, At >= Output.Length);
            }
        }

        [Fact]
        public void QueryBinary_RawReplyCutOffByATimeout_FailsRatherThanReturningAShortFile()
        {
            var t = new BlockTransport { Output = new byte[300], FallsSilent = true };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() =>
                mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", 30000, BlockFormat.Raw));

            Assert.Contains("timed out", ex.Message);
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

        private static readonly IdentitySpec ScpiErrors = new IdentitySpec { ErrorQuery = ":SYST:ERR? STR" };

        /// <summary>The 54845A's reaction to a query it rejects: it sends nothing, and logs why.</summary>
        private sealed class RejectingTransport : FakeTransportBase
        {
            public readonly System.Collections.Generic.Queue<string> Errors =
                new System.Collections.Generic.Queue<string>(new[] { "-224,\"Illegal parameter value\"", "-420,\"Query UNTERMINATED\"" });
            public readonly System.Collections.Generic.List<int> ReadTimeouts = new System.Collections.Generic.List<int>();
            public readonly System.Collections.Generic.List<string> Written = new System.Collections.Generic.List<string>();
            public int ErrorReadMs;

            public override void Write(string resource, byte[] payload, int timeoutMs) =>
                Written.Add(System.Text.Encoding.ASCII.GetString(payload));

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                ReadTimeouts.Add(request.TimeoutMs);
                if (Written[Written.Count - 1].StartsWith(":SYST:ERR?"))
                {
                    System.Threading.Thread.Sleep(ErrorReadMs);
                    return new TransportReadResult(System.Text.Encoding.ASCII.GetBytes((Errors.Count > 0 ? Errors.Dequeue() : "0,\"No error\"") + "\n"), false, true);
                }
                return new TransportReadResult(System.Array.Empty<byte>(), true);
            }
        }

        [Fact]
        public void QueryBinary_ARejectedCommand_FailsAtTheShortFirstWait_AndReportsTheInstrumentsError()
        {
            // The hang that blocked the server: a 60 s read, with every later call queued behind it.
            var t = new RejectingTransport();
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", ":DISP:DATA? GIF,SCR,COL", 60000, BlockFormat.Ieee4882,
                new BinaryReadOptions { FirstReplyMs = 3000, Identity = ScpiErrors, DeviceClearSafe = true }));

            Assert.Equal(3000, t.ReadTimeouts[0]);
            Assert.Equal(1, t.ClearCount);
            Assert.Contains("sent nothing within 3000 ms", ex.Message);
            Assert.Contains("-224,\"Illegal parameter value\"; -420,\"Query UNTERMINATED\"", ex.Message);
            Assert.Contains("device clear was sent", ex.Message);
        }

        [Fact]
        public void QueryBinary_NeverDeviceClears_AModelThatDoesntSaySoItsSafe()
        {
            // On the 8560 series a device clear presets the instrument, wiping the user's setup.
            var t = new RejectingTransport();
            var mgr = new InstrumentManager(t);

            Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::18::INSTR", "DUMP?", 30000, BlockFormat.Ieee4882,
                new BinaryReadOptions { Identity = ScpiErrors }));

            Assert.Equal(0, t.ClearCount);
        }

        [Fact]
        public void QueryBinary_NoReply_WithAnEmptyErrorQueue_SaysItMayJustBeSlow()
        {
            var t = new RejectingTransport();
            t.Errors.Clear();
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 30000, BlockFormat.Ieee4882,
                new BinaryReadOptions { Identity = ScpiErrors }));

            Assert.Contains("first_reply_timeout_ms", ex.Message);
        }

        [Fact]
        public void QueryBinary_NoReply_WithNoModel_SaysTheErrorQueueWasntRead()
        {
            var mgr = new InstrumentManager(new RejectingTransport());

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 30000, BlockFormat.Ieee4882));

            Assert.Contains("error queue wasn't read", ex.Message);
        }

        /// <summary>Sends a block in pieces with pauses, each a read that times out: a slow instrument, not a dead one.</summary>
        private sealed class PausingTransport : FakeTransportBase
        {
            public byte[] Output;
            public int At, Pauses;
            public int SilentReadsPerPause = 3;
            public int PiecesSent;

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                if (At > 0 && At < Output.Length && Pauses++ % (SilentReadsPerPause + 1) != SilentReadsPerPause)
                    return new TransportReadResult(System.Array.Empty<byte>(), true);
                int n = (int)System.Math.Min(request.MaxBytes, System.Math.Min(40, Output.Length - At));
                var chunk = new byte[n];
                System.Array.Copy(Output, At, chunk, 0, n);
                At += n;
                PiecesSent++;
                return new TransportReadResult(chunk, At < Output.Length, At >= Output.Length);   // "timed out", with data
            }
        }

        [Fact]
        public void QueryBinary_AReadThatTimesOutWithData_IsASlowTransfer_NotAFailure()
        {
            var t = new PausingTransport { Output = System.Text.Encoding.ASCII.GetBytes("#3300").Concat(new byte[300]).ToArray() };
            var mgr = new InstrumentManager(t);

            BinaryBlockResult block = mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", 20000, BlockFormat.Ieee4882);

            Assert.Equal(300, block.Payload.Length);
        }

        [Fact]
        public void QueryBinary_ThatGoesSilentPartWay_GivesUpAfterTheTimeout()
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#41000").Concat(new byte[100]).ToArray(), FallsSilent = true };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", 4000, BlockFormat.Ieee4882));

            Assert.Contains("timed out", ex.Message);
            // 6 for the header, 2 for the data, 4 silent 1 s slices making the 4 s timeout, 1 to drain.
            Assert.Equal(6 + 2 + 4 + 1, t.Requests.Count);
        }

        [Theory]
        [InlineData(1500, new[] { 1000, 500 })]   // was one 1 s slice: rounded down to whole seconds
        [InlineData(500, new[] { 500 })]          // was a full second
        [InlineData(3000, new[] { 1000, 1000, 1000 })]
        public void QueryBinary_ThatGoesSilentPartWay_WaitsTheTimeoutAskedFor_NotWholeSeconds(int timeoutMs, int[] silentWaits)
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#41000").Concat(new byte[100]).ToArray(), FallsSilent = true };
            var mgr = new InstrumentManager(t);

            Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", timeoutMs, BlockFormat.Ieee4882));

            // 6 for the header, 2 for the data, then the silence, then 1 to drain.
            Assert.Equal(silentWaits, t.Requests.Skip(8).Take(silentWaits.Length).Select(r => r.TimeoutMs).ToArray());
            Assert.Equal(8 + silentWaits.Length + 1, t.Requests.Count);
        }

        [Fact]
        public void QueryBinary_RawReply_WaitsShortlyForOneByte_ThenLongForTheRest()
        {
            // A first read of a whole buffer would time out on a slow transfer that hadn't filled it yet.
            var t = new BlockTransport { Output = new byte[300] };
            var mgr = new InstrumentManager(t);

            BinaryBlockResult block = mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", 20000, BlockFormat.Raw, new BinaryReadOptions { FirstReplyMs = 3000 });

            Assert.Equal(300, block.Payload.Length);
            Assert.Equal(1, t.Requests[0].MaxBytes);
            Assert.Equal(3000, t.Requests[0].TimeoutMs);
            Assert.InRange(t.Requests[1].TimeoutMs, 1, 1000);
        }

        [Fact]
        public void QueryBinary_WaitsOnceForTheFirstByte_ThenReadsInShortSlices()
        {
            // Never split that first wait: re-addressing the 54845A while it built the image failed every capture,
            // and left the scope answering nothing, not even a serial poll, until it was power-cycled.
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#3100").Concat(new byte[100]).Concat(new[] { (byte)'\n' }).ToArray() };
            var mgr = new InstrumentManager(t);

            mgr.QueryBinary("GPIB0::17::INSTR", "DUMP?", 20000, BlockFormat.Ieee4882, new BinaryReadOptions { FirstReplyMs = 3000 });

            Assert.Equal(3000, t.Requests[0].TimeoutMs);
            Assert.All(t.Requests.Skip(1), r => Assert.InRange(r.TimeoutMs, 1, 1000));
        }

        [Fact]
        public void QueryBinary_ALongFirstWait_IsLimited_SoADriversStepsCantCarryItPastTheCallsLimit()
        {
            // 20 s waits about 42 s over a GPIB-ENET/1000, far past the default 25 s.
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#14DATA\n") };
            var mgr = new InstrumentManager(t);

            mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 30000, BlockFormat.Ieee4882, new BinaryReadOptions { FirstReplyMs = 20000 });

            Assert.Equal(3000, t.Requests[0].TimeoutMs);    // the next step down, about 4.2 s, fits in 11 s
        }

        [Theory]
        [InlineData(3000, 11000, 3000)]     // the 54845A's default: about 4.2 s, well inside
        [InlineData(5000, 6000, 3000)]      // 5000 waits about 17 s, so it drops to 3000
        [InlineData(5000, 26000, 5000)]
        [InlineData(20000, 11000, 3000)]    // 20000 waits about 42 s
        [InlineData(2000, 500, 300)]
        public void TheFirstRead_IsLimitedToAStepWhoseRealWaitFits(int requested, int limit, int expected)
        {
            Assert.Equal(expected, InstrumentManager.FirstReadMs(requested, limit));
        }

        [Fact]
        public void QueryBinary_TheFirstReplyWait_IsNotCappedByTheTimeoutForASilentReply()
        {
            // timeout_ms is about a reply going silent; how long one may take to start is a separate figure.
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#14DATA\n") };
            var mgr = new InstrumentManager(t);

            mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882, new BinaryReadOptions { FirstReplyMs = 10000, TotalMs = 55000 });

            Assert.Equal(10000, t.Requests[0].TimeoutMs);
        }

        [Fact]
        public void QueryBinary_WithNoOptions_ANarrowTimeoutStillLimitsTheFirstWait()
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("#14DATA\n") };
            var mgr = new InstrumentManager(t);

            mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882);

            Assert.Equal(1000, t.Requests[0].TimeoutMs);
        }

        [Fact]
        public void QueryBinary_ThatLeavesARecoveryShortOfTime_StopsReadingTheErrorQueue()
        {
            var t = new RejectingTransport { ErrorReadMs = 400 };
            t.Errors.Enqueue("-1,\"One\"");
            t.Errors.Enqueue("-2,\"Two\"");
            t.Errors.Enqueue("-3,\"Three\"");
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882,
                new BinaryReadOptions { FirstReplyMs = 100, TotalMs = 1000, Identity = ScpiErrors }));

            Assert.Contains("there may be more", ex.Message);
            Assert.True(t.Errors.Count > 0, "read every error");
        }

        /// <summary>Text arriving in slow reads that time out with data, then stopping without END.</summary>
        private sealed class StalledTextTransport : FakeTransportBase
        {
            public int Chunks = 3;

            public override TransportReadResult Read(string resource, TransportReadRequest request) =>
                Chunks-- > 0 ? new TransportReadResult(new[] { (byte)'1', (byte)'2' }, true, false)
                             : new TransportReadResult(System.Array.Empty<byte>(), true, false);
        }

        [Fact]
        public void QueryBinary_AReplyThatStopsPartWayThrough_StillGetsTheDeviceClear()
        {
            // A timeout with data is a slow transfer, not the end of one, and more may be coming.
            var t = new StalledTextTransport();
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882,
                new BinaryReadOptions { DeviceClearSafe = true }));

            Assert.Equal(1, t.ClearCount);
            Assert.Contains("device clear was sent", ex.Message);
        }

        /// <summary>Sends <see cref="First"/> without END, then silence, or a stream that never ends.</summary>
        private sealed class UnendedTransport : FakeTransportBase
        {
            public byte[] First = { (byte)'-' };
            public bool Streams;
            public readonly System.Collections.Generic.List<string> Written = new System.Collections.Generic.List<string>();
            private bool _sent;

            public override void Write(string resource, byte[] payload, int timeoutMs) =>
                Written.Add(System.Text.Encoding.ASCII.GetString(payload));

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                if (!_sent) { _sent = true; return new TransportReadResult(First, false, false); }
                if (!Streams) return new TransportReadResult(System.Array.Empty<byte>(), true, false);
                System.Threading.Thread.Sleep(20);
                return new TransportReadResult(new[] { (byte)'x' }, true, false);
            }
        }

        [Fact]
        public void QueryBinary_AReplyThatStartedAndWentSilentWithoutEnd_StillGetsTheDeviceClear()
        {
            var t = new UnendedTransport();
            var mgr = new InstrumentManager(t);

            Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882,
                new BinaryReadOptions { DeviceClearSafe = true }));

            Assert.Equal(1, t.ClearCount);
        }

        [Fact]
        public void QueryBinary_AnInstrumentStillSending_IsntAskedForItsErrors()
        {
            // No clear allowed, and it never stops: a query now would only interrupt the reply.
            var t = new UnendedTransport { Streams = true };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882,
                new BinaryReadOptions { TotalMs = 1000, Identity = ScpiErrors }));

            Assert.Equal(0, t.ClearCount);
            Assert.DoesNotContain(t.Written, w => w.StartsWith(":SYST:ERR?"));
            Assert.Contains("may still be sending", ex.Message);
        }

        [Fact]
        public void QueryBinary_AReplyThatEndedProperly_NeedsNoDeviceClear()
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("12\n"), MaxPerRead = 1 };
            var mgr = new InstrumentManager(t);

            Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882,
                new BinaryReadOptions { DeviceClearSafe = true }));

            Assert.Equal(0, t.ClearCount);
        }

        [Fact]
        public void QueryBinary_KeepsOnlyTheStartOfADiscardedReply()
        {
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("1" + new string('y', 5000) + "\n") };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 1000, BlockFormat.Ieee4882));

            Assert.Contains("5001 more bytes", ex.Message);
            Assert.Contains(new string('y', 64), ex.Message);
            Assert.DoesNotContain(new string('y', 65), ex.Message);
        }

        [Fact]
        public void QueryBinary_ThatRunsOutOfTime_StopsAtTheLimit_WhateverTheReadTimeout()
        {
            var t = new TricklingTransport();
            var mgr = new InstrumentManager(t);
            var clock = System.Diagnostics.Stopwatch.StartNew();

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 60000, BlockFormat.Ieee4882,
                new BinaryReadOptions { TotalMs = 1000 }));

            Assert.InRange(clock.ElapsedMilliseconds, 900, 2900);
            Assert.All(t.ReadTimeouts, ms => Assert.True(ms <= 1000, ms + " ms"));
            Assert.Contains("limit on the call ran out", ex.Message);
        }

        /// <summary>Declares a large block, then sends it a little at a time, for ever.</summary>
        private sealed class TricklingTransport : FakeTransportBase
        {
            private readonly System.IO.MemoryStream _header = new System.IO.MemoryStream(System.Text.Encoding.ASCII.GetBytes("#71000000"));
            public readonly System.Collections.Generic.List<int> ReadTimeouts = new System.Collections.Generic.List<int>();

            public override TransportReadResult Read(string resource, TransportReadRequest request)
            {
                ReadTimeouts.Add(request.TimeoutMs);
                if (_header.Position < _header.Length) return new TransportReadResult(new[] { (byte)_header.ReadByte() }, false, false);
                System.Threading.Thread.Sleep(50);
                return new TransportReadResult(new byte[10], false, false);
            }
        }

        [Fact]
        public void QueryBinary_ATextReply_QuotesWhatItSent_ForTheCaller()
        {
            // The 54845A's old "screen dump": ASCII waveform values, which said nothing with only the '-' reported.
            var t = new BlockTransport { Output = System.Text.Encoding.ASCII.GetBytes("-1.23E-01,4.56E-01\n") };
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", ":WAVeform:DATA?", 30000, BlockFormat.Ieee4882));

            Assert.Contains("1.23E-01,4.56E-01", ex.Message);
            Assert.Equal(t.Output.Length, t.At);
        }

        [Fact]
        public void QueryBinary_AModelThatCanBeCleared_IsClearedIfItWontStopSending()
        {
            var t = new TricklingTransport();
            var mgr = new InstrumentManager(t);

            var ex = Assert.Throws<GpibOperationException>(() => mgr.QueryBinary("GPIB0::7::INSTR", "DUMP?", 30000, BlockFormat.Raw,
                new BinaryReadOptions { TotalMs = 1000, DeviceClearSafe = true }));

            Assert.Equal(1, t.ClearCount);
            Assert.Contains("device clear was sent", ex.Message);
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
