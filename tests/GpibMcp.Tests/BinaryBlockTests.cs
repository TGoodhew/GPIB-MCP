using System;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #139: binary replies read as bytes, never text - header parsed as it arrives, exactly the declared
    /// length read over as many reads as it takes, the trailing terminator consumed - and a bad block
    /// reported with what arrived.
    /// </summary>
    public class BinaryBlockTests
    {
        /// <summary>An instrument's output buffer, read up to N bytes at a time (at most <see cref="MaxPerRead"/>).</summary>
        private sealed class Instrument
        {
            private readonly byte[] _out;
            private int _at;
            public int MaxPerRead = 7;   // small, so every block spans many reads

            /// <summary>Never ends its message: once the output runs out, the next read times out.</summary>
            public bool FallsSilent;
            public bool TimedOut;

            public Instrument(byte[] output) { _out = output; }
            public int Left => _out.Length - _at;

            public byte[] Read(int count, out bool more)
            {
                int n = Math.Min(Math.Min(count, MaxPerRead), Left);
                byte[] chunk = _out.Skip(_at).Take(n).ToArray();
                _at += n;
                TimedOut = FallsSilent && n == 0;
                more = FallsSilent ? !TimedOut : Left > 0;
                return chunk;
            }

            public BinaryBlockResult ReadBlock(BlockFormat format) => BinaryBlock.Read(Read, format, BinaryBlock.MaxBytes, () => TimedOut);
        }

        private static byte[] Bytes(string s) => Encoding.ASCII.GetBytes(s);

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        /// <summary>A GIF-like payload that contains 0x0A, as image data does.</summary>
        private static readonly byte[] Gif = Concat(Bytes("GIF89a"), new byte[] { 0x80, 0x02, 0x0A, 0xE0, 0x01, 0x0A, 0x0A, 0x00, 0xFF });

        private static string Header(int digits, int length) => "#" + digits + length.ToString().PadLeft(digits, '0');

        [Theory]
        [InlineData(2)]   // the payload is 15 bytes, so at least two length digits
        [InlineData(5)]
        [InlineData(9)]
        public void ADefiniteLengthBlock_IsReadExactly_IncludingNewlinesInThePayload(int digits)
        {
            var instrument = new Instrument(Concat(Bytes(Header(digits, Gif.Length)), Gif, Bytes("\n")));

            BinaryBlockResult block = BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882);

            Assert.Equal(Gif, block.Payload);
            Assert.Equal(Header(digits, Gif.Length), block.Header);
            Assert.Equal(Gif.Length, block.DeclaredLength);
            Assert.Equal(0, instrument.Left);   // the trailing LF was read too: nothing left for the next command
        }

        [Fact]
        public void AOneDigitHeader_ReadsJustTheBytesItDeclares()
        {
            var instrument = new Instrument(Concat(Bytes("#16"), Gif.Take(6).ToArray(), Bytes("\n")));
            Assert.Equal(Bytes("GIF89a"), BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882).Payload);
        }

        [Fact]
        public void ABlockWithoutATrailingNewline_IsReadAndStops()
        {
            var instrument = new Instrument(Concat(Bytes(Header(2, Gif.Length)), Gif));
            Assert.Equal(Gif, BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882).Payload);
        }

        [Fact]
        public void LeadingWhitespace_BeforeTheHeader_IsSkipped()
        {
            var instrument = new Instrument(Concat(Bytes(" \r\n" + Header(3, Gif.Length)), Gif, Bytes("\n")));
            Assert.Equal(Gif, BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882).Payload);
        }

        [Fact]
        public void AnIndefiniteBlock_RunsToTheEnd_LessItsNewline()
        {
            var instrument = new Instrument(Concat(Bytes("#0"), Gif, Bytes("\n")));

            BinaryBlockResult block = BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882);

            Assert.Equal(Gif, block.Payload);
            Assert.Equal("#0", block.Header);
            Assert.Null(block.DeclaredLength);
        }

        [Fact]
        public void AShortBlock_SaysHowMuchArrived_OfHowMuchItDeclared()
        {
            // Declares 100 bytes; the instrument stops after 16.
            var instrument = new Instrument(Concat(Bytes("#3100"), Gif, Gif.Take(7).ToArray()));

            var ex = Assert.Throws<BinaryBlockException>(() => BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882));

            Assert.Equal(Gif.Length + 7, ex.BytesRead);
            Assert.Equal(100, ex.BytesExpected);
            Assert.Equal("#3100", ex.Header);
            Assert.StartsWith("47 49 46 38 39 61", ex.FirstBytesHex);   // "GIF89a"
            Assert.Contains("of 100 expected", ex.Message);
        }

        [Fact]
        public void AReplyThatIsNotABlock_IsReported_WithItsFirstBytes()
        {
            var instrument = new Instrument(Bytes("+0,\"No error\"\n"));

            var ex = Assert.Throws<BinaryBlockException>(() => BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882));

            Assert.Contains("doesn't start with an IEEE 488.2 block header", ex.Message);
            Assert.Equal("2B", ex.FirstBytesHex);   // '+'
        }

        [Fact]
        public void AHeaderWithoutADigit_IsReported()
        {
            var instrument = new Instrument(Bytes("#A123"));
            var ex = Assert.Throws<BinaryBlockException>(() => BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882));
            Assert.Contains("isn't followed by a digit", ex.Message);
        }

        [Fact]
        public void RawMode_ReadsUnframedBytesToTheEnd()
        {
            var instrument = new Instrument(Gif);

            BinaryBlockResult block = BinaryBlock.Read(instrument.Read, BlockFormat.Raw);

            Assert.Equal(Gif, block.Payload);
            Assert.Null(block.Header);
        }

        [Fact]
        public void ACrLfTerminator_IsConsumed()
        {
            var instrument = new Instrument(Concat(Bytes(Header(2, Gif.Length)), Gif, Bytes("\r\n")));
            Assert.Equal(Gif, BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882).Payload);
            Assert.Equal(0, instrument.Left);
        }

        [Fact]
        public void DataAfterTheDeclaredLength_IsReported_NotSilentlyDiscarded()
        {
            // The header understates the length: what follows isn't a terminator.
            var instrument = new Instrument(Concat(Bytes(Header(2, Gif.Length)), Gif, Bytes("XYZ\n")));

            var ex = Assert.Throws<BinaryBlockException>(() => BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882));

            Assert.Contains("declared length is wrong", ex.Message);
            Assert.Contains("58", ex.Message);   // 'X'
        }

        [Theory]
        [InlineData(BlockFormat.Raw)]
        [InlineData(BlockFormat.Ieee4882)]   // as "#0"
        public void AReplyToEndThatTimesOut_IsAnError_NotAShortFile(BlockFormat format)
        {
            byte[] sent = format == BlockFormat.Raw ? Gif : Concat(Bytes("#0"), Gif);
            var instrument = new Instrument(sent) { FallsSilent = true };

            var ex = Assert.Throws<BinaryBlockException>(() => instrument.ReadBlock(format));

            Assert.Contains("timed out", ex.Message);
            Assert.Equal(Gif.Length, ex.BytesRead);
        }

        [Fact]
        public void ADefiniteBlockThatTimesOutPartWay_SaysItTimedOut()
        {
            var instrument = new Instrument(Concat(Bytes("#3100"), Gif)) { FallsSilent = true };

            var ex = Assert.Throws<BinaryBlockException>(() => instrument.ReadBlock(BlockFormat.Ieee4882));

            Assert.Contains("timed out", ex.Message);
            Assert.Equal(Gif.Length, ex.BytesRead);
            Assert.Equal(100, ex.BytesExpected);
        }

        [Fact]
        public void ADefiniteBlock_WhollyRead_ThenSilence_IsAccepted()
        {
            // Every declared byte arrived; only END never came. The length proves the payload complete.
            var instrument = new Instrument(Concat(Bytes(Header(2, Gif.Length)), Gif)) { FallsSilent = true };
            Assert.Equal(Gif, instrument.ReadBlock(BlockFormat.Ieee4882).Payload);
        }

        [Fact]
        public void NoReplyAtAll_SaysItTimedOut()
        {
            var instrument = new Instrument(new byte[0]) { FallsSilent = true };
            var ex = Assert.Throws<BinaryBlockException>(() => instrument.ReadBlock(BlockFormat.Ieee4882));
            Assert.Contains("timed out", ex.Message);
        }

        [Fact]
        public void ALargeBlock_SpanningManyReads_IsReadWhole()
        {
            byte[] image = Enumerable.Range(0, 200000).Select(i => (byte)(i * 31)).ToArray();   // plenty of 0x0A
            var instrument = new Instrument(Concat(Bytes(Header(6, image.Length)), image, Bytes("\n"))) { MaxPerRead = 4096 };

            Assert.Equal(image, BinaryBlock.Read(instrument.Read, BlockFormat.Ieee4882).Payload);
            Assert.Equal(0, instrument.Left);
        }
    }
}
