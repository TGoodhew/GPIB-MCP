using System;
using System.Collections.Generic;
using System.Linq;
using GpibMcp.Instruments;
using Ivi.Visa;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Tests for the GPIB/VISA error-detail + command-chain diagnostics (issue #11):
    /// the bounded command history, VISA status decoding, and the enriched exception.
    /// </summary>
    public class GpibDiagnosticsTests
    {
        // ---- CommandHistory ------------------------------------------------------

        [Fact]
        public void History_IsBounded_KeepingMostRecent()
        {
            var history = new CommandHistory(depth: 3);
            for (int i = 0; i < 6; i++)
                history.Record("GPIB0::5::INSTR", CommandDirection.Sent, "CMD" + i);

            var snap = history.Snapshot("GPIB0::5::INSTR");
            Assert.Equal(3, snap.Count);
            Assert.Equal(new[] { "CMD3", "CMD4", "CMD5" }, snap.Select(e => e.Text));
        }

        [Fact]
        public void History_DoesNotLeakAcrossResources()
        {
            var history = new CommandHistory(depth: 10);
            history.Record("GPIB0::5::INSTR", CommandDirection.Sent, "A");
            history.Record("GPIB0::7::INSTR", CommandDirection.Sent, "B");

            Assert.Equal(new[] { "A" }, history.Snapshot("GPIB0::5::INSTR").Select(e => e.Text));
            Assert.Equal(new[] { "B" }, history.Snapshot("GPIB0::7::INSTR").Select(e => e.Text));
            Assert.Empty(history.Snapshot("GPIB0::9::INSTR"));
        }

        [Fact]
        public void History_Snapshot_RespectsMax()
        {
            var history = new CommandHistory(depth: 10);
            for (int i = 0; i < 5; i++) history.Record("R", CommandDirection.Sent, "C" + i);
            Assert.Equal(new[] { "C3", "C4" }, history.Snapshot("R", 2).Select(e => e.Text));
        }

        [Fact]
        public void HistoryEntry_ToLine_ShowsDirectionArrowAndEscapedText()
        {
            var sent = new CommandHistoryEntry("R", CommandDirection.Sent, "FREQ 1e6\n", DateTime.UtcNow);
            var recv = new CommandHistoryEntry("R", CommandDirection.Received, "OK\r\n", DateTime.UtcNow);
            Assert.Contains("-> \"FREQ 1e6\\n\"", sent.ToLine());
            Assert.Contains("<- \"OK\\r\\n\"", recv.ToLine());
        }

        // ---- VISA status decoding ------------------------------------------------

        [Fact]
        public void DescribeCode_DecodesCommonStatuses()
        {
            Assert.Equal("VI_ERROR_TMO", VisaErrorInfo.DescribeCode(unchecked((int)0xBFFF0015)).Name);
            Assert.Equal("VI_ERROR_NLISTENERS", VisaErrorInfo.DescribeCode(unchecked((int)0xBFFF005F)).Name);
            Assert.Contains("Timeout", VisaErrorInfo.DescribeCode(unchecked((int)0xBFFF0015)).Meaning);
        }

        [Fact]
        public void DescribeCode_UnknownStatus_GetsHexName()
        {
            var info = VisaErrorInfo.DescribeCode(unchecked((int)0xBFFF00FF));
            Assert.True(info.HasName);
            Assert.Contains("BFFF00FF", info.Name);
        }

        [Fact]
        public void Describe_Timeout_Exception_MapsToTmo()
        {
            var info = VisaErrorInfo.Describe(new IOTimeoutException(0, Array.Empty<byte>()));
            Assert.Equal("VI_ERROR_TMO", info.Name);
        }

        [Fact]
        public void Describe_NativeException_DecodesErrorCode()
        {
            var info = VisaErrorInfo.Describe(new NativeVisaException(unchecked((int)0xBFFF005F)));
            Assert.Equal("VI_ERROR_NLISTENERS", info.Name);
        }

        [Fact]
        public void Describe_NonVisaException_HasNoName()
        {
            Assert.False(VisaErrorInfo.Describe(new InvalidOperationException("boom")).HasName);
        }

        // ---- GpibOperationException ---------------------------------------------

        private static IReadOnlyList<CommandHistoryEntry> SampleHistory() => new List<CommandHistoryEntry>
        {
            new CommandHistoryEntry("GPIB0::18::INSTR", CommandDirection.Sent, "SNGLS;TS;\n", DateTime.UtcNow),
            new CommandHistoryEntry("GPIB0::18::INSTR", CommandDirection.Sent, "MKPK HI;\n", DateTime.UtcNow),
        };

        [Fact]
        public void Exception_Summary_NamesResourceCommandAndDecodedStatus()
        {
            var ex = GpibOperationException.For(GpibOperation.Query, "GPIB0::18::INSTR", "MKPK HI?",
                new NativeVisaException(unchecked((int)0xBFFF0015)), SampleHistory());

            Assert.Equal("VI_ERROR_TMO", ex.VisaStatusName);
            Assert.Contains("GPIB0::18::INSTR", ex.Message);
            Assert.Contains("MKPK HI?", ex.Message);
            Assert.Contains("VI_ERROR_TMO", ex.Message);
            Assert.Contains("query failed", ex.Message);
        }

        [Fact]
        public void Exception_Detail_IncludesTheCommandChain()
        {
            var ex = GpibOperationException.For(GpibOperation.Query, "GPIB0::18::INSTR", "MKPK HI?",
                new NativeVisaException(unchecked((int)0xBFFF0015)), SampleHistory());

            string detail = ex.Detail;
            Assert.Contains("Recent command chain", detail);
            Assert.Contains("SNGLS;TS;", detail);
            Assert.Contains("MKPK HI;", detail);
            Assert.StartsWith(ex.Message, detail); // summary first, then the chain
        }

        [Fact]
        public void Exception_NonVisaInner_FallsBackToInnerMessage()
        {
            var ex = GpibOperationException.For(GpibOperation.Write, "GPIB0::5::INSTR", "OUTP ON",
                new InvalidOperationException("driver exploded"), Array.Empty<CommandHistoryEntry>());

            Assert.Null(ex.VisaStatusName);
            Assert.Contains("driver exploded", ex.Message);
            Assert.DoesNotContain("Recent command chain", ex.Detail); // no history -> no chain section
        }

        // ---- verbatim ("exact codes and text") detail ----------------------------

        [Fact]
        public void DescribeCode_CarriesRawCode()
        {
            Assert.Equal(unchecked((int)0xBFFF0015), VisaErrorInfo.DescribeCode(unchecked((int)0xBFFF0015)).Code);
            Assert.Equal(unchecked((int)0xBFFF005F),
                VisaErrorInfo.Describe(new NativeVisaException(unchecked((int)0xBFFF005F))).Code);
        }

        [Fact]
        public void Exception_VerboseDetail_HasDecodedNameRawCodeInnerTextAndChain()
        {
            var ex = GpibOperationException.For(GpibOperation.Query, "GPIB0::18::INSTR", "MKPK HI?",
                new NativeVisaException(unchecked((int)0xBFFF0015), "raw driver text"), SampleHistory());

            Assert.Equal(unchecked((int)0xBFFF0015), ex.VisaStatusCode);

            string v = ex.VerboseDetail;
            Assert.Contains("VI_ERROR_TMO", v);                                  // decoded name
            Assert.Contains("0xBFFF0015", v);                                    // raw code, hex
            Assert.Contains(unchecked((int)0xBFFF0015).ToString(), v);           // raw code, decimal
            Assert.Contains("NativeVisaException", v);                           // underlying exception type
            Assert.Contains("raw driver text", v);                              // underlying message, verbatim
            Assert.Contains("MKPK HI;", v);                                      // command chain present
        }
    }
}
