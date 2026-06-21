using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class CommandTextTests
    {
        [Theory]
        [InlineData("*IDN?", "*IDN?\n")]
        [InlineData("*IDN?\n", "*IDN?\n")]
        [InlineData("", "\n")]
        [InlineData(null, "\n")]
        public void EnsureTerminated_AppendsSingleTerminator(string input, string expected)
        {
            Assert.Equal(expected, CommandText.EnsureTerminated(input));
        }

        [Fact]
        public void EnsureTerminated_DoesNotDoubleTerminate()
        {
            Assert.Equal("MEAS:VOLT?\n", CommandText.EnsureTerminated("MEAS:VOLT?\n"));
        }

        [Theory]
        [InlineData("*IDN?", "\r\n", "*IDN?\r\n")]
        [InlineData("*IDN?\r\n", "\r\n", "*IDN?\r\n")]   // not double-terminated
        [InlineData("*IDN?", null, "*IDN?\n")]            // null falls back to default LF
        [InlineData("*IDN?", "", "*IDN?\n")]              // empty falls back to default LF
        public void EnsureTerminated_HonoursCustomTerminator(string input, string terminator, string expected)
        {
            Assert.Equal(expected, CommandText.EnsureTerminated(input, terminator));
        }

        [Theory]
        [InlineData("\n", '\n')]
        [InlineData("\r\n", '\n')]   // last char of the sequence
        [InlineData("\r", '\r')]
        public void ReadTerminatorChar_TakesLastCharacter(string read, char expected)
        {
            var spec = new TerminationSpec { Read = read };
            Assert.Equal(expected, spec.ReadTerminatorChar());
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void ReadTerminatorChar_NullWhenUnset(string read)
        {
            Assert.Null(new TerminationSpec { Read = read }.ReadTerminatorChar());
        }

        [Theory]
        [InlineData("*IDN?\n", "\"*IDN?\\n\"")]
        [InlineData("a\r\nb", "\"a\\r\\nb\"")]
        [InlineData("", "\"\"")]
        [InlineData(null, "\"\"")]
        public void ForLog_EscapesControlCharacters(string input, string expected)
        {
            Assert.Equal(expected, CommandText.ForLog(input));
        }
    }
}
