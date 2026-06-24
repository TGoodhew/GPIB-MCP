using System.Collections.Generic;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class BatchTimingLogTests
    {
        [Fact]
        public void Format_OrdersOpsByTotalTimeDescending_AndReportsSharesAndErrors()
        {
            var result = new BatchResult
            {
                Ok = true,
                Ran = new BatchRanInfo { Sweep = "f 1..3 step 1", Points = 3, OpsPerPoint = 3, GpibOps = 9, ElapsedMs = 1000 },
                Timing = new List<BatchOpTiming>
                {
                    new BatchOpTiming { Op = "write",    Count = 3, TotalMs = 100, MaxMs = 40 },
                    new BatchOpTiming { Op = "complete", Count = 3, TotalMs = 800, MaxMs = 300 },  // the hotspot
                    new BatchOpTiming { Op = "query",    Count = 3, TotalMs = 100, MaxMs = 50 }
                },
                Errors = { new BatchError { Op = "query", Error = "timeout" } }
            };

            string text = BatchTimingLog.Format(result, timestamp: "2026-06-24 13:00:00");

            // header carries the sweep + ran summary
            Assert.Contains("==== batch 2026-06-24 13:00:00  f 1..3 step 1 ====", text);
            Assert.Contains("3 points, 3 ops/point, 9 gpib ops, 1000ms total", text);
            // hotspot (complete, 800ms) is listed before the cheaper ops
            int idxComplete = text.IndexOf("complete");
            int idxWrite = text.IndexOf("write");
            Assert.True(idxComplete >= 0 && idxWrite > idxComplete, "complete should precede write (sorted by total desc)");
            // share is computed against the summed op time (800/1000 = 80%)
            Assert.Contains("80.0%", text);
            Assert.Contains("errors: 1", text);
        }

        [Fact]
        public void Format_NoOps_StatesNothingCompleted()
        {
            var result = new BatchResult { Ran = new BatchRanInfo { Points = 1, GpibOps = 0, ElapsedMs = 0 } };
            string text = BatchTimingLog.Format(result, timestamp: "2026-06-24 13:00:00");
            Assert.Contains("per-op timing: (no ops completed)", text);
            Assert.Contains("errors: 0", text);
        }
    }
}
