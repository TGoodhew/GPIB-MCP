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

        // ---- time budget (#145) ----------------------------------------------------

        /// <summary>An executor on a virtual clock: each I/O op takes OpMs, an operation OperationMs to complete.</summary>
        private sealed class ClockedExec : IBatchExecutor
        {
            public long Now;
            public int OpMs = 1000;
            public int OperationMs = 2000;
            public readonly List<string> Log = new List<string>();
            public int LastCompleteTimeoutMs;

            public void Write(string r, string c, int t) { Now += OpMs; Log.Add("W " + c); }
            public string Query(string r, string c, int t) { Now += OpMs; Log.Add("Q " + c); return "-42.5"; }
            public void Set(string r, string c, double v, string u, int t) { Now += OpMs; Log.Add("S " + v); }
            public void Sleep(int ms) { Now += ms; Log.Add("Z " + ms); }
            public void Complete(string r, string op, int t)
            {
                LastCompleteTimeoutMs = t;
                Log.Add("C " + op + " " + t);
                if (OperationMs <= t) { Now += OperationMs; return; }
                Now += t;
                throw new Exception("Timed out after " + t + " ms waiting for '" + op + "'.");
            }
        }

        private static BatchResult Run(BatchPlan plan, ClockedExec exec) =>
            BatchRunner.Run(plan, exec, new BatchCaps(), () => exec.Now);

        [Fact]
        public void ASweepThatWouldOutlastTheBudget_StopsWithTheRowsItHas_AndSaysWhereToContinue()
        {
            // #145: a batch that ran past the client's tool-call limit lost its whole result. Now it stops in time.
            var plan = new BatchPlan
            {
                Sweep = new BatchSweep { Var = "f", From = 1, To = 10, Step = 1 },
                Steps = new List<BatchStep> { new BatchStep { Op = "query", Resource = "A", Command = "MKA?", As = "amp" } }
            };
            var exec = new ClockedExec { OpMs = 4000 };   // 10 points x 4 s = 40 s, against the default 25 s

            var result = Run(plan, exec);

            Assert.False(result.Ok);
            Assert.Equal(7, result.Rows.Count);           // points started before 25 s ran out: 0..6
            Assert.Equal(7, result.Stopped.AtPoint);
            Assert.Equal(8d, result.Stopped.AtValue);     // continue the sweep from f = 8
            Assert.Null(result.Stopped.Waiting);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ACompleteWait_IsCutShortAtTheBudget_AndTheResultSaysTheOperationMayStillBeRunning()
        {
            var plan = new BatchPlan
            {
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "write", Resource = "A", Command = "ST 60S;" },
                    new BatchStep { Op = "complete", Resource = "A", Operation = "sweepComplete", TimeoutMs = 90000 },
                    new BatchStep { Op = "query", Resource = "A", Command = "MKA?", As = "amp" }
                }
            };
            var exec = new ClockedExec { OperationMs = 60000 };

            var result = Run(plan, exec);

            Assert.Equal(24000, exec.LastCompleteTimeoutMs);       // the 25 s budget less the 1 s write
            Assert.False(result.Ok);
            Assert.Equal("sweepComplete", result.Stopped.Waiting);
            Assert.Contains("time budget ran out while waiting for 'sweepComplete'", Assert.Single(result.Errors).Error);
            Assert.Empty(result.Rows);                             // the point never finished
            Assert.DoesNotContain("Q MKA?", exec.Log);
            Assert.True(exec.Now <= 25000);                        // returned inside the budget
        }

        [Fact]
        public void ACompleteThatFinishesInsideTheBudget_IsUnaffected()
        {
            var plan = new BatchPlan
            {
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "complete", Resource = "A", Operation = "sweepComplete" },
                    new BatchStep { Op = "query", Resource = "A", Command = "MKA?", As = "amp" }
                }
            };
            var result = Run(plan, new ClockedExec { OperationMs = 2000 });

            Assert.True(result.Ok);
            Assert.Null(result.Stopped);
            Assert.Equal(-42.5, Assert.Single(result.Rows)[0]);
        }

        [Fact]
        public void AWaitLongerThanTheBudget_IsCutShort_AndStops()
        {
            var plan = new BatchPlan
            {
                TimeBudgetMs = 10000,
                Steps = new List<BatchStep> { new BatchStep { Op = "wait", SettleMs = 40000 } }
            };
            var exec = new ClockedExec();

            var result = Run(plan, exec);

            Assert.Equal(new[] { "Z 10000" }, exec.Log);
            Assert.False(result.Ok);
            Assert.NotNull(result.Stopped);
            Assert.Empty(result.Errors);
        }

        [Fact]
        public void ALargerBudget_LetsALongerBatchRun()
        {
            var plan = new BatchPlan
            {
                TimeBudgetMs = 55000,
                Sweep = new BatchSweep { Var = "f", From = 1, To = 10, Step = 1 },
                Steps = new List<BatchStep> { new BatchStep { Op = "query", Resource = "A", Command = "MKA?", As = "amp" } }
            };
            var result = Run(plan, new ClockedExec { OpMs = 4000 });

            Assert.True(result.Ok);
            Assert.Equal(10, result.Rows.Count);
        }

        [Theory]
        [InlineData(500)]
        [InlineData(60000)]
        public void ABudgetOutsideOneTo55Seconds_IsRejected(int budgetMs)
        {
            var plan = new BatchPlan { TimeBudgetMs = budgetMs, Steps = new List<BatchStep> { new BatchStep { Op = "wait", SettleMs = 1 } } };
            Assert.Contains("time budget", BatchRunner.Validate(plan, new BatchCaps()));
        }

        // ---- reply cells (#141) ---------------------------------------------------

        [Theory]
        [InlineData("005,403,409,420,421,422,503,602,UNJ")]   // *OPT?: was captured as 5
        [InlineData("+16,\"Input Overload\"")]                // :SYST:ERR?: was 16, the message lost
        [InlineData("+0,\"No error\"")]
        [InlineData("-7.42E+001,-7.59E+001,-7.61E+001")]      // a trace: was its first point
        [InlineData("HP8563E,006")]                           // an identity: was 8563
        [InlineData("HP3325B")]
        [InlineData("OVERLOAD")]
        public void AReplyThatIsNotASingleNumber_IsKeptWhole(string reply)
        {
            Assert.Equal(reply, BatchRunner.ParseCell(reply + "\n"));
        }

        [Theory]
        [InlineData("+5.00000000E+008", 500000000d)]
        [InlineData(" -42.33\r\n", -42.33)]
        [InlineData("1000000", 1000000d)]
        [InlineData("FA 1.5E6 HZ", 1500000d)]                 // an older HP instrument's header-and-unit reply
        [InlineData("-12.5DBM", -12.5)]
        public void AReplyThatIsASingleNumber_IsANumericCell(string reply, double value)
        {
            Assert.Equal(value, BatchRunner.ParseCell(reply));
        }

        [Fact]
        public void QueryCells_KeepWholeReplies_AndNumbersStillFeedLaterSteps()
        {
            var plan = new BatchPlan
            {
                Steps = new List<BatchStep>
                {
                    new BatchStep { Op = "query", Resource = "E4438C", Command = "*OPT?", As = "opt" },
                    new BatchStep { Op = "query", Resource = "E4438C", Command = ":FREQ?", As = "freq" },
                    new BatchStep { Op = "write", Resource = "E4406A", Command = ":FREQ:CENT {{freq}}" },
                    new BatchStep { Op = "query", Resource = "E4406A", Command = ":SYST:ERR?", As = "err" }
                }
            };
            var fake = new FakeExec
            {
                OnQuery = (r, c) => c == "*OPT?" ? "005,403,409,UNJ\n" : c == ":FREQ?" ? "+5.00000000E+008\n" : "+16,\"Input Overload\"\n"
            };

            var result = Run(plan, fake);

            var row = Assert.Single(result.Rows);
            var columns = result.Columns.Select(c => c.Name).ToList();
            Assert.Equal("005,403,409,UNJ", row[columns.IndexOf("opt")]);
            Assert.Equal(500000000d, row[columns.IndexOf("freq")]);
            Assert.Equal("+16,\"Input Overload\"", row[columns.IndexOf("err")]);
            Assert.Contains("W E4406A :FREQ:CENT 500000000", fake.Log);   // the numeric capture interpolates
        }

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
