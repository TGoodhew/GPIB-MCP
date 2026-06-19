// -----------------------------------------------------------------------------
// Tests for Hpgl.Rendering (parser).
//
// The HP-GL plotter-emulation technique is derived from the HP7470A Plotter
// Emulator (7470.cpp) by John Miles, KE5FX - http://www.ke5fx.com/
// -----------------------------------------------------------------------------

using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    // The parser is internal; it is reachable via [InternalsVisibleTo].
    public class HpglParserTests
    {
        [Fact]
        public void Parse_SplitsMnemonicsAndNumbers()
        {
            var instr = HpglParser.Parse("PU0,0;PD100,200;SP2;");
            Assert.Equal(3, instr.Count);

            Assert.Equal("PU", instr[0].Mnemonic);
            Assert.Equal(new double[] { 0, 0 }, instr[0].Parameters);

            Assert.Equal("PD", instr[1].Mnemonic);
            Assert.Equal(new double[] { 100, 200 }, instr[1].Parameters);

            Assert.Equal("SP", instr[2].Mnemonic);
            Assert.Equal(2.0, instr[2].Parameters[0]);
        }

        [Fact]
        public void Parse_HandlesMissingSemicolons()
        {
            var instr = HpglParser.Parse("PA100,100PD200,200");
            Assert.Equal(2, instr.Count);
            Assert.Equal("PA", instr[0].Mnemonic);
            Assert.Equal("PD", instr[1].Mnemonic);
            Assert.Equal(new double[] { 200, 200 }, instr[1].Parameters);
        }

        [Fact]
        public void Parse_ReadsLabelToEtxTerminator()
        {
            string src = "SP1;LBHello World" + ((char)3) + "PU0,0;";
            var instr = HpglParser.Parse(src);

            Assert.Equal("LB", instr[1].Mnemonic);
            Assert.Equal("Hello World", instr[1].Text);
            Assert.Equal("PU", instr[2].Mnemonic); // parsing resumes after the terminator
        }

        [Fact]
        public void Parse_DtChangesLabelTerminator()
        {
            // DT* sets '*' as the label terminator for subsequent LB instructions.
            var instr = HpglParser.Parse("DT*;LBfreq*PU0,0;");
            Assert.Equal("LB", instr[1].Mnemonic);
            Assert.Equal("freq", instr[1].Text);
            Assert.Equal("PU", instr[2].Mnemonic);
        }

        [Fact]
        public void Parse_HandlesNegativeAndDecimalNumbers()
        {
            var instr = HpglParser.Parse("PA-100,-50.5;");
            Assert.Equal(new double[] { -100, -50.5 }, instr[0].Parameters);
        }
    }
}
