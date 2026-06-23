using System.Collections.Generic;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class ScreenCaptureTests
    {
        // ---- scripted fake channel (no hardware) --------------------------------

        private sealed class Step
        {
            public byte[] Data;
            public bool TimedOut;
            public long AdvanceMs;

            public static Step Data_(string s, long adv = 10) =>
                new Step { Data = Latin1(s), TimedOut = false, AdvanceMs = adv };

            public static Step Timeout(long adv = 200, string partial = null) =>
                new Step { Data = partial == null ? null : Latin1(partial), TimedOut = true, AdvanceMs = adv };
        }

        private sealed class FakeChannel : ICaptureChannel
        {
            public readonly List<string> Sent = new List<string>();
            public long NowMs;
            private readonly Queue<Step> _steps;

            public FakeChannel(IEnumerable<Step> steps) { _steps = new Queue<Step>(steps); }

            public void Send(string text) => Sent.Add(text);

            public CaptureRead Read()
            {
                if (_steps.Count == 0) { NowMs += 1000; return new CaptureRead(System.Array.Empty<byte>(), true); }
                var s = _steps.Dequeue();
                NowMs += s.AdvanceMs;
                return new CaptureRead(s.Data ?? System.Array.Empty<byte>(), s.TimedOut);
            }
        }

        private static byte[] Latin1(string s) => Encoding.GetEncoding("ISO-8859-1").GetBytes(s);

        private static CaptureResult Run(FakeChannel ch, CaptureOptions opt = null, string preRoll = null,
                                         string plot = "PLOT 550,279,9750,7479;") =>
            ScreenCapture.Run(ch, preRoll, plot, opt ?? new CaptureOptions(), () => ch.NowMs);

        private static string Bytes(int n, string suffix)
        {
            var sb = new StringBuilder();
            while (sb.Length < n) sb.Append("PD1,1;");
            return sb.ToString() + suffix;
        }

        // ---- tests --------------------------------------------------------------

        [Fact]
        public void SendsPrerollAndPlotCommand()
        {
            var ch = new FakeChannel(new[] { Step.Data_(Bytes(130, "SP0;")), Step.Timeout() });
            Run(ch, preRoll: "SNGLS;TS;");
            Assert.Equal("SNGLS;TS;", ch.Sent[0]);
            Assert.Equal("PLOT 550,279,9750,7479;", ch.Sent[1]);
        }

        [Fact]
        public void AnswersOs_24ThenFollowedBy16_AndStripsOpcode()
        {
            var ch = new FakeChannel(new[]
            {
                Step.Data_("SP1;PU;PA1,1;OS"), Step.Timeout(),   // first OS -> 24
                Step.Data_("PD2,2;OS"),        Step.Timeout(),   // second OS -> 16
                Step.Data_(Bytes(130, "SP0;")), Step.Timeout()   // finish
            });
            var result = Run(ch);

            var replies = ch.Sent.Where(s => s == "24\r\n" || s == "16\r\n").ToList();
            Assert.Equal(new[] { "24\r\n", "16\r\n" }, replies);     // 24 first, then 16
            Assert.Equal(CaptureCompletion.PenUp, result.Completion);
            Assert.DoesNotContain("OS", result.Hpgl);               // answered opcodes stripped
        }

        [Fact]
        public void CompletesOnPenUp_Sp0InTail()
        {
            var ch = new FakeChannel(new[] { Step.Data_(Bytes(140, "SP0;")), Step.Timeout(adv: 10) });
            var result = Run(ch);
            Assert.Equal(CaptureCompletion.PenUp, result.Completion);
            Assert.True(result.ByteCount >= 128);
            Assert.EndsWith("SP0;", result.Hpgl);
        }

        [Fact]
        public void CompletesOnInactivity_WhenNoPenUp()
        {
            var ch = new FakeChannel(new[]
            {
                Step.Data_(Bytes(140, "PU;")),       // >= min bytes, no SP0
                Step.Timeout(adv: 4000)              // idle exceeds 3500 ms
            });
            var result = Run(ch);
            Assert.Equal(CaptureCompletion.Inactivity, result.Completion);
        }

        [Fact]
        public void SubThresholdBuffer_KeepsWaiting_UntilBackstop()
        {
            var opt = new CaptureOptions { InactivityTimeoutMs = 100, OverallTimeoutMs = 1000 };
            var ch = new FakeChannel(new[]
            {
                Step.Data_("tiny"),         // < 128 bytes: must NOT complete on inactivity
                Step.Timeout(adv: 200),     // idle > 100 but buffer too small -> keep waiting
                Step.Timeout(adv: 1000)     // overall backstop exceeded
            });
            var result = Run(ch, opt);
            Assert.Equal(CaptureCompletion.Backstop, result.Completion);
            Assert.Equal(4, result.ByteCount);
        }

        [Fact]
        public void OeReply_OnlyWhenConfigured()
        {
            // Not configured (default): OE is not answered.
            var ch1 = new FakeChannel(new[]
            {
                Step.Data_("PA1,1;OE"), Step.Timeout(),
                Step.Data_(Bytes(130, "SP0;")), Step.Timeout()
            });
            Run(ch1);
            Assert.DoesNotContain("0\r\n", ch1.Sent);

            // Configured: OE is answered with the supplied reply.
            var ch2 = new FakeChannel(new[]
            {
                Step.Data_("PA1,1;OE"), Step.Timeout(),
                Step.Data_(Bytes(130, "SP0;")), Step.Timeout()
            });
            Run(ch2, new CaptureOptions { OeReply = "0" });
            Assert.Contains("0\r\n", ch2.Sent);
        }

        [Theory]
        [InlineData("OA", "500,500,0")]
        [InlineData("OC", "1,2,0")]
        [InlineData("OF", "40,40")]
        public void OaOcOf_OnlyAnsweredWhenConfigured(string op, string reply)
        {
            // Default: the query is not answered - only the plot command was sent.
            var unconfigured = new FakeChannel(new[]
            {
                Step.Data_("PA1,1;" + op), Step.Timeout(),
                Step.Data_(Bytes(130, "SP0;")), Step.Timeout()
            });
            Run(unconfigured);
            Assert.Equal(new[] { "PLOT 550,279,9750,7479;" }, unconfigured.Sent);

            // Configured: the query is answered with the supplied reply (+ terminator).
            var configured = new FakeChannel(new[]
            {
                Step.Data_("PA1,1;" + op), Step.Timeout(),
                Step.Data_(Bytes(130, "SP0;")), Step.Timeout()
            });
            var options = new CaptureOptions();
            if (op == "OA") options.OaReply = reply;
            else if (op == "OC") options.OcReply = reply;
            else if (op == "OF") options.OfReply = reply;
            Run(configured, options);
            Assert.Contains(reply + "\r\n", configured.Sent);
        }

        // ---- #53 capture-loop diagnostics --------------------------------------

        [Fact]
        public void Diagnostics_SplitWarmupStreamAndTail_AndTraceEveryRead()
        {
            // read0: 100ms to first data; read1: a full-chunk burst at +50ms whose tail holds the pen-up
            // (a full read looks mid-burst, so it is NOT early-completed); read2: 200ms empty timeout
            // detects the pen-up -> done. This exercises the warm-up / stream / tail split with a real tail.
            string fullChunk = new string('A', 4092) + "SP0;";  // exactly ReadChunkSize (4096)
            var ch = new FakeChannel(new[]
            {
                Step.Data_(Bytes(130, ""), adv: 100),
                Step.Data_(fullChunk, adv: 50),
                Step.Timeout(adv: 200),
            });
            var d = Run(ch).Diagnostics;

            Assert.NotNull(d);
            Assert.Equal(3, d.TotalReads);
            Assert.Equal(2, d.DataReads);
            Assert.Equal(1, d.TimedOutReads);
            Assert.Equal(100, d.PreRollToFirstByteMs);   // command -> first byte (warm-up)
            Assert.Equal(50, d.StreamMs);                // first byte -> last byte
            Assert.Equal(200, d.TailMs);                 // last byte -> completion
            Assert.Equal(350, d.TotalMs);
            Assert.Equal(350, d.TotalTimeInReadsMs);     // all time was inside Read() here
            Assert.Equal(Bytes(130, "").Length + fullChunk.Length, d.TotalBytes);

            Assert.Equal(3, d.Reads.Count);              // every read is traced
            Assert.Equal(100, d.Reads[0].ElapsedMs);
            Assert.True(d.Reads[2].TimedOut);
            Assert.Contains("over 2 reads", d.SummaryLine("plot"));
            Assert.Contains("1 timeouts", d.SummaryLine("plot"));
        }

        [Fact]
        public void Plot_FinishesImmediately_WhenShortEoiReadShowsPenUp()
        {
            // The #53 tail fix: a short, EOI-terminated (non-timeout) read carrying the pen-up completes
            // at once - we do NOT wait for a following empty read to time out.
            var ch = new FakeChannel(new[]
            {
                Step.Data_(Bytes(130, ""), adv: 50),   // bulk data (not a timeout, but a full block)
                Step.Data_("SP0;", adv: 20),           // short EOI read with pen-up -> complete now
            });
            var r = Run(ch);
            Assert.Equal(CaptureCompletion.PenUp, r.Completion);
            Assert.Equal(2, r.Diagnostics.TotalReads);   // finished on read #1 - no extra timeout read
            Assert.Equal(70, r.Diagnostics.TotalMs);     // 50 + 20, no ~PerReadTimeout tail
        }

        [Fact]
        public void DefaultPerReadTimeout_IsTrimmedForFasterWarmupAndTail()
        {
            Assert.Equal(250, new CaptureOptions().PerReadTimeoutMs);   // was 1000 (#53)
        }

        [Fact]
        public void TimingLog_Format_HasSummaryAndPerReadTrace()
        {
            var ch = new FakeChannel(new[]
            {
                Step.Data_(Bytes(130, ""), adv: 100),
                Step.Data_("PD2,2;SP0;", adv: 50),
                Step.Timeout(adv: 200),
            });
            var result = Run(ch);

            string entry = CaptureTimingLog.Format("GPIB0::18::INSTR", "8563E", "plot",
                "PLOT 550,279,9750,7479;", result, renderMs: 6, saveMs: 3, timestamp: "TEST-TS");

            Assert.Contains("==== capture TEST-TS  8563E (plot)  GPIB0::18::INSTR ====", entry);
            Assert.Contains("command: PLOT 550,279,9750,7479;", entry);
            Assert.Contains("render 6ms, save 3ms", entry);
            Assert.Contains("per-read trace", entry);
            Assert.Contains("#0", entry);          // first read traced
            Assert.Contains("[timeout]", entry);   // the terminating read flagged
        }
    }
}
