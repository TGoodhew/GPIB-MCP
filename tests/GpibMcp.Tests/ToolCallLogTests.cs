using GpibMcp.Diagnostics;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    public class ToolCallLogTests
    {
        [Fact]
        public void Format_ScalarArgs_AreInlinedWithStatusAndElapsed()
        {
            var args = new JObject { ["resource"] = "GPIB0::10::INSTR", ["command"] = "FR 500000HZ" };

            string line = ToolCallLog.Format("visa_write", args, ok: true, elapsedMs: 8, timestamp: "2026-06-24 13:45:01.123");

            Assert.StartsWith("2026-06-24 13:45:01.123", line);
            Assert.Contains("ok", line);
            Assert.Contains("8ms", line);
            Assert.Contains("visa_write", line);
            Assert.Contains("resource=GPIB0::10::INSTR", line);
            Assert.Contains("command=FR 500000HZ", line);
            Assert.EndsWith(System.Environment.NewLine, line);
        }

        [Fact]
        public void Format_Error_IsMarkedErr()
        {
            string line = ToolCallLog.Format("visa_identify", new JObject { ["resource"] = "GPIB0::18::INSTR" },
                ok: false, elapsedMs: 12);
            Assert.Contains("ERR", line);
            Assert.DoesNotContain(" ok ", line);
        }

        [Fact]
        public void Format_ArraysAndObjects_AreSummarisedByCount()
        {
            // a gpib_batch-shaped call: steps is an array, sweep is an object - digest them compactly, not in full.
            var args = new JObject
            {
                ["sweep"] = new JObject { ["var"] = "f", ["from"] = 1, ["to"] = 10, ["step"] = 1 },
                ["steps"] = new JArray { new JObject(), new JObject(), new JObject() },
                ["confirm"] = true
            };

            string line = ToolCallLog.Format("gpib_batch", args, ok: true, elapsedMs: 412);

            Assert.Contains("steps=[3]", line);     // array -> [count]
            Assert.Contains("sweep={4}", line);      // object -> {key count}
            Assert.Contains("confirm=True", line);
            Assert.Contains("gpib_batch", line);
        }

        [Fact]
        public void Format_NoArgs_ShowsDash()
        {
            Assert.Contains(" -" + System.Environment.NewLine, ToolCallLog.Format("gpib_overview", new JObject(), true, 1));
        }
    }
}
