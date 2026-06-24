using System;
using System.Collections.Generic;
using System.Linq;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class BatchRunnerTests
    {
        // ---- scripted fake executor (no hardware) -------------------------------

        private sealed class FakeExec : IBatchExecutor
        {
            public readonly List<string> Log = new List<string>();
            public Func<string, string, string> OnQuery;   // (resource, command) -> response
            public int ThrowAtOp = -1;                     // throw on this 0-based global op
            private int _op;

            private void Tick()
            {
                int n = _op++;
                if (n == ThrowAtOp) throw new Exception("boom@" + n);
            }
            public void Write(string r, string c, int t) { Tick(); Log.Add("W " + r + " " + c); }
            public string Query(string r, string c, int t) { Tick(); Log.Add("Q " + r + " " + c); return OnQuery != null ? OnQuery(r, c) : "0"; }
            public void Set(string r, string c, double v, string u, int t) { Tick(); Log.Add("S " + r + " " + c + "=" + v + u); }
            public void Complete(string r, string op, int t) { Tick(); Log.Add("C " + r + " " + op); }
            public void Sleep(int ms) { Log.Add("Z " + ms); }
        }

        private static BatchResult Run(BatchPlan plan, FakeExec fake, BatchCaps caps = null) =>
            BatchRunner.Run(plan, fake, caps ?? new BatchCaps(), () => 0);

        // ---- sweep expansion ----------------------------------------------------

        [Fact]
        public void ExpandSweep_StepAndCount_AreInclusive()
        {
            var byStep = BatchRunner.ExpandSweep(new BatchSweep { Var = "f", From = 500000, To = 20000000, Step = 500000 }, out var e1);
            Assert.Null(e1);
            Assert.Equal(40, byStep.Count);                 // 0.5..20 MHz inclusive
            Assert.Equal(500000, byStep.First());
            Assert.Equal(20000000, byStep.Last());

            var byCount = BatchRunner.ExpandSweep(new BatchSweep { Var = "f", From = 0, To = 10, Count = 6 }, out var e2);
            Assert.Null(e2);
            Assert.Equal(new[] { 0d, 2, 4, 6, 8, 10 }, byCount);

            BatchRunner.ExpandSweep(new BatchSweep { Var = "f", From = 0, To = 10, Step = 0 }, out var e3);
            Assert.Contains("non-zero", e3);
        }

        // ---- the acceptance scenario (generic) ----------------------------------

        [Fact]
        public void AcceptanceSweep_SetWriteCompleteQuery_ProducesTable()
        {
            var plan = new BatchPlan
            {
                Sweep = new BatchSweep { Var = "f_hz", From = 500000, To = 1500000, Step = 500000, Unit = "Hz" }, // 3 pts
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "set",      Resource = "3325B", Command = "frequency", Value = "{{f_hz}}", Unit = "Hz" },
                    new BatchStep { Op = "write",    Resource = "8563E", Command = "CF {{f_hz}}HZ;" },
                    new BatchStep { Op = "complete", Resource = "8563E", Operation = "sweepComplete" },
                    new BatchStep { Op = "write",    Resource = "8563E", Command = "MKPK HI;" },
                    new BatchStep { Op = "query",    Resource = "8563E", Command = "MKF?", As = "freq_hz" },
                    new BatchStep { Op = "query",    Resource = "8563E", Command = "MKA?", As = "amp_dbm" }
                },
                OnError = "continue"
            };
            var fake = new FakeExec { OnQuery = (r, c) => c == "MKF?" ? "1000000" : "-12.3" };

            var res = Run(plan, fake);

            Assert.True(res.Ok);
            Assert.Equal(new[] { "f_hz", "freq_hz", "amp_dbm" }, res.Columns.Select(c => c.Name));
            Assert.Equal("Hz", res.Columns[0].Unit);
            Assert.Equal(3, res.Rows.Count);
            Assert.Equal(new object[] { 500000d, 1000000d, -12.3 }, res.Rows[0]);
            Assert.Equal(3, res.Ran.Points);
            Assert.Equal(18, res.Ran.GpibOps);                       // 3 points x 6 ops
            Assert.Empty(res.Errors);
            // interpolation + the per-op dispatch actually happened:
            Assert.Contains("S 3325B frequency=500000Hz", fake.Log);
            Assert.Contains("W 8563E CF 500000HZ;", fake.Log);       // {{f_hz}} substituted
            Assert.Equal(3, fake.Log.Count(l => l == "C 8563E sweepComplete"));
        }

        [Fact]
        public void Timing_AggregatesWallClockPerOpType()
        {
            var plan = new BatchPlan
            {
                Sweep = new BatchSweep { Var = "f", From = 1, To = 3, Step = 1 },   // 3 pts
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "set",      Resource = "A", Command = "frequency", Value = "{{f}}", Unit = "Hz" },
                    new BatchStep { Op = "write",    Resource = "A", Command = "CF {{f}}HZ;" },
                    new BatchStep { Op = "complete", Resource = "A", Operation = "sweepComplete" },
                    new BatchStep { Op = "query",    Resource = "A", Command = "MKA?", As = "amp" }
                }
            };
            // A clock that advances 10ms per read, so per-op deltas are deterministic and non-zero.
            long t = 0;
            var res = BatchRunner.Run(plan, new FakeExec { OnQuery = (r, c) => "-1" }, new BatchCaps(), () => t += 10);

            // one bucket per distinct op type (first-seen order), each run once per point (3 points).
            Assert.Equal(new[] { "set", "write", "complete", "query" }, res.Timing.Select(x => x.Op));
            foreach (var op in res.Timing)
            {
                Assert.Equal(3, op.Count);                 // ran at all 3 sweep points
                Assert.True(op.TotalMs > 0);               // wall-clock attributed
                Assert.True(op.MaxMs >= op.MeanMs);
            }
        }

        // ---- capture feeds a later step -----------------------------------------

        [Fact]
        public void CapturedValue_InterpolatesIntoLaterStep()
        {
            var plan = new BatchPlan
            {
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "query", Resource = "A", Command = "READ?", As = "v" },
                    new BatchStep { Op = "write", Resource = "B", Command = "SET {{v}}" }
                }
            };
            var fake = new FakeExec { OnQuery = (r, c) => "42" };
            Run(plan, fake);
            Assert.Contains("W B SET 42", fake.Log);
        }

        // ---- numeric parse of responses -----------------------------------------

        [Fact]
        public void QueryResponses_ParseToNumbers_ElseRawString()
        {
            var plan = new BatchPlan
            {
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "query", Resource = "A", Command = "F?", As = "f" },
                    new BatchStep { Op = "query", Resource = "A", Command = "S?", As = "s" }
                }
            };
            var fake = new FakeExec { OnQuery = (r, c) => c == "F?" ? "FA 1.5E6 HZ" : "OVERLOAD" };
            var res = Run(plan, fake);
            Assert.Equal(1500000d, res.Rows[0][0]);    // leading number extracted
            Assert.Equal("OVERLOAD", res.Rows[0][1]);  // non-numeric kept raw
        }

        // ---- error handling -----------------------------------------------------

        [Fact]
        public void OnErrorContinue_RecordsError_AndKeepsGoing()
        {
            var plan = SimpleTwoPointQuery();
            plan.OnError = "continue";
            var fake = new FakeExec { ThrowAtOp = 0 };   // first point's only op fails
            var res = Run(plan, fake);
            Assert.False(res.Ok);
            Assert.Single(res.Errors);
            Assert.Equal(0, res.Errors[0].Point);
            Assert.Equal(2, res.Rows.Count);             // both points still produced a row (point 2 ok)
        }

        [Fact]
        public void OnErrorStop_AbortsRun()
        {
            var plan = SimpleTwoPointQuery();
            plan.OnError = "stop";
            var fake = new FakeExec { ThrowAtOp = 0 };
            var res = Run(plan, fake);
            Assert.False(res.Ok);
            Assert.Single(res.Errors);
            Assert.Single(res.Rows);                     // aborted after point 1
        }

        // ---- caps ---------------------------------------------------------------

        [Fact]
        public void RowCap_TruncatesAndReports()
        {
            var plan = new BatchPlan
            {
                Sweep = new BatchSweep { Var = "i", From = 1, To = 10, Step = 1 },
                Steps = new List<BatchStep> { new BatchStep { Op = "write", Resource = "A", Command = "X" } }
            };
            var res = BatchRunner.Run(plan, new FakeExec(), new BatchCaps { MaxRows = 4 }, () => 0);
            Assert.Equal(4, res.Rows.Count);
            Assert.NotNull(res.Truncated);
            Assert.Equal(10, res.Truncated.Total);
        }

        [Fact]
        public void Validate_RejectsOverCaps()
        {
            var plan = new BatchPlan
            {
                Sweep = new BatchSweep { Var = "i", From = 1, To = 100, Step = 1 },
                Steps = new List<BatchStep> { new BatchStep { Op = "write", Resource = "A", Command = "X" } }
            };
            Assert.Contains("points", BatchRunner.Validate(plan, new BatchCaps { MaxPoints = 10 }));
            Assert.Null(BatchRunner.Validate(plan, new BatchCaps()));
        }

        private static BatchPlan SimpleTwoPointQuery() => new BatchPlan
        {
            Sweep = new BatchSweep { Var = "i", From = 1, To = 2, Step = 1 },
            Steps = new List<BatchStep> { new BatchStep { Op = "query", Resource = "A", Command = "R?", As = "v" } }
        };
    }
}
