using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class Ieee4882BlockTests
    {
        private static byte[] A(string s) => Encoding.ASCII.GetBytes(s);
        private static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        [Fact]
        public void Definite_OneDigitLength_ReturnsPayload()
        {
            // #14ABCD : n=1, len=4, then 4 data bytes
            var payload = Ieee4882Block.ExtractDefiniteLength(A("#14ABCD"));
            Assert.Equal(A("ABCD"), payload);
        }

        [Fact]
        public void Definite_MultiDigitLength_ReturnsExactBytes()
        {
            // #210 + 10 bytes
            var block = Cat(A("#210"), A("0123456789"));
            Assert.Equal(A("0123456789"), Ieee4882Block.ExtractDefiniteLength(block));
        }

        [Fact]
        public void Definite_IgnoresBytesBeyondDeclaredLength()
        {
            // Length says 4; trailing junk (and a terminator) must be dropped.
            var payload = Ieee4882Block.ExtractDefiniteLength(A("#14ABCDtrailing-junk"));
            Assert.Equal(A("ABCD"), payload);
        }

        [Fact]
        public void Definite_TrimsLeadingWhitespaceBeforeHash()
        {
            var payload = Ieee4882Block.ExtractDefiniteLength(A("\r\n#14ABCD"));
            Assert.Equal(A("ABCD"), payload);
        }

        [Fact]
        public void Indefinite_Hash0_ReturnsToEnd_TrimmingOneNewline()
        {
            // #0 = indefinite; data runs to the end, one trailing CRLF trimmed.
            var payload = Ieee4882Block.ExtractDefiniteLength(A("#0ABCDEF\r\n"));
            Assert.Equal(A("ABCDEF"), payload);
        }

        [Fact]
        public void NoHeader_ReturnsInputUnchanged()
        {
            // Some instruments emit the raw image with no IEEE framing.
            var raw = new byte[] { 0x42, 0x4D, 0x01, 0x02 }; // "BM..." (a BMP magic)
            Assert.Equal(raw, Ieee4882Block.ExtractDefiniteLength(raw));
        }

        [Fact]
        public void Truncated_TakesOnlyWhatArrived()
        {
            // Declares 9 bytes but only 8 follow -> return the 8 present (never read past the buffer).
            var payload = Ieee4882Block.ExtractDefiniteLength(A("#1900012345"));
            Assert.Equal(A("00012345"), payload);
        }

        [Fact]
        public void BinaryPayload_WithEmbeddedNewlines_PreservedExactly()
        {
            // Image bytes contain 0x0A/0x0D etc.; the length field (not a terminator) bounds the read.
            var data = new byte[] { 0x89, 0x50, 0x0A, 0x0D, 0x00, 0xFF };
            var block = Cat(A("#16"), data);
            Assert.Equal(data, Ieee4882Block.ExtractDefiniteLength(block));
        }
    }
}
