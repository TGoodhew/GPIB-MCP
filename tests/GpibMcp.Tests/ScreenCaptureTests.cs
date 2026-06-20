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
    }
}
