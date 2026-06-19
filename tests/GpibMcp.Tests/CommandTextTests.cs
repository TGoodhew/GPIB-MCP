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
