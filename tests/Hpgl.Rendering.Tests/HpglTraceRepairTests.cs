// -----------------------------------------------------------------------------
// Tests for HpglTraceRepair (#79): single-point trace X excursion repair.
// -----------------------------------------------------------------------------

using System.Globalization;
using System.Text;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class HpglTraceRepairTests
    {
        // A long pen-down trace: X is a regular increasing grid (step 14), Y varies like a real sweep.
        // Optionally corrupt one pair's X token to simulate a dropped/added digit on the GPIB read.
        private static string Trace(int pairs, int corruptIndex = -1, string corruptXText = null,
                                    int corruptYIndex = -1, string corruptYText = null)
        {
            var sb = new StringBuilder("IN;SP2;PU100,500;PD;PA");
            for (int k = 0; k < pairs; k++)
            {
                int x = 100 + k * 14;
                int y = 500 + (k * 37) % 300;
                if (k > 0) sb.Append(',');
                sb.Append(k == corruptIndex && corruptXText != null ? corruptXText : x.ToString(CultureInfo.InvariantCulture));
                sb.Append(',');
                sb.Append(k == corruptYIndex && corruptYText != null ? corruptYText : y.ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(";PU0,0;SP0;");
            return sb.ToString();
        }

        [Fact]
        public void Repair_RestoresDroppedDigitExcursion_ToNeighbourMidpoint()
        {
            // Point 10 has true X = 240; drop a digit -> "24" so the pen jumps backwards then recovers.
            // Neighbours are 226 and 254, midpoint 240 - the true grid X.
            var hpgl = Trace(50, corruptIndex: 10, corruptXText: "24");
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(1, repaired);
            Assert.DoesNotContain(",24,", fixedHpgl);   // the excursion token is gone
            Assert.Contains(",240,", fixedHpgl);        // restored to the neighbour midpoint
        }

        [Fact]
        public void Repair_CatchesForwardRunOnSpike()
        {
            // A digit run-on makes X jump far forward (2409) then recover - also outside the neighbour
            // bracket, so it is restored to the midpoint.
            var hpgl = Trace(50, corruptIndex: 10, corruptXText: "2409");
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(1, repaired);
            Assert.DoesNotContain(",2409,", fixedHpgl);
            Assert.Contains(",240,", fixedHpgl);
        }

        [Fact]
        public void Repair_LeavesACleanTraceUnchanged()
        {
            var hpgl = Trace(50);
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(0, repaired);
            Assert.Equal(hpgl, fixedHpgl);
        }

        [Fact]
        public void Repair_DoesNotTouchAmplitudeSpike()
        {
            // A corrupted Y is indistinguishable from a real signal peak - X stays monotonic, so we leave it.
            var hpgl = Trace(50, corruptYIndex: 10, corruptYText: "9999");
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(0, repaired);
            Assert.Equal(hpgl, fixedHpgl);
        }

        [Fact]
        public void Repair_DoesNotTouchRightToLeftGraticule()
        {
            // Graticule horizontals legitimately run right-to-left; they are short, so never treated as a
            // trace - backwards X here must be preserved byte-for-byte.
            var hpgl = "IN;SP1;PU9577,6845;PD5265,6845;PD952,6845;PU0,0;SP0;";
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(0, repaired);
            Assert.Equal(hpgl, fixedHpgl);
        }

        [Fact]
        public void Repair_PreservesLabelsWithDigits_WhileFixingTheTrace()
        {
            // A label's digits ("300Hz") must never be mistaken for coordinates, and the trace after it is
            // still repaired.
            var label = "SP3;PU100,7000;LBRBW 300Hz  SWP 1.40sec" + (char)3 + ";";
            var hpgl = "IN;" + label + Trace(50, corruptIndex: 10, corruptXText: "24");
            var fixedHpgl = HpglTraceRepair.Repair(hpgl, out int repaired);

            Assert.Equal(1, repaired);
            Assert.Contains(label, fixedHpgl);          // label untouched
            Assert.Contains(",240,", fixedHpgl);
        }

        [Fact]
        public void Repair_NullOrEmpty_IsSafe()
        {
            Assert.Null(HpglTraceRepair.Repair(null, out int r1));
            Assert.Equal(0, r1);
            Assert.Equal("", HpglTraceRepair.Repair("", out int r2));
            Assert.Equal(0, r2);
        }
    }
}
