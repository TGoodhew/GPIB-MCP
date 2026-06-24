using GpibMcp.Tools;
using Xunit;

namespace GpibMcp.Tests
{
    public class BatchLoopNudgeTests
    {
        [Fact]
        public void Nudges_OnceTheSingleOpRunReachesTheThreshold()
        {
            var n = new BatchLoopNudge(threshold: 3, repeatEvery: 4);
            Assert.Null(n.Observe("visa_write"));        // 1
            Assert.Null(n.Observe("visa_query"));        // 2
            Assert.NotNull(n.Observe("visa_write"));     // 3 -> nudge
        }

        [Fact]
        public void Batch_ResetsTheRun()
        {
            var n = new BatchLoopNudge(threshold: 2);
            n.Observe("visa_write");                     // 1
            Assert.NotNull(n.Observe("visa_write"));     // 2 -> nudge
            Assert.Null(n.Observe("gpib_batch"));        // the model batched -> reset
            Assert.Equal(0, n.Run);
            Assert.Null(n.Observe("visa_write"));        // 1 again, below threshold
        }

        [Fact]
        public void NonLoopTools_AreIgnored_NeitherCountedNorReset()
        {
            var n = new BatchLoopNudge(threshold: 2);
            n.Observe("visa_write");                     // 1
            Assert.Null(n.Observe("gpib_overview"));     // ignored
            Assert.Equal(1, n.Run);                      // not counted
            Assert.NotNull(n.Observe("visa_query"));     // 2 -> nudge (overview didn't reset the run)
        }

        [Fact]
        public void Repeats_EveryNthCallPastTheThreshold()
        {
            var n = new BatchLoopNudge(threshold: 2, repeatEvery: 2);
            Assert.Null(n.Observe("visa_write"));        // 1
            Assert.NotNull(n.Observe("visa_write"));     // 2 -> nudge
            Assert.Null(n.Observe("visa_write"));        // 3
            Assert.NotNull(n.Observe("visa_write"));     // 4 -> nudge
        }

        [Fact]
        public void Message_NamesGpibBatch_AndTheRunLength()
        {
            var n = new BatchLoopNudge(threshold: 1);
            string msg = n.Observe("visa_write");
            Assert.Contains("gpib_batch", msg);
            Assert.Contains("1 single-op", msg);
        }
    }
}
