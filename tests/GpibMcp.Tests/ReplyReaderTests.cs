using System.Collections.Generic;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// #141: a text reply is read to its end however many reads it takes, and one too long to keep is
    /// discarded rather than left on the instrument.
    /// </summary>
    public class ReplyReaderTests
    {
        /// <summary>An instrument's output buffer, read the way a transport reads it: up to N bytes at a time.</summary>
        private sealed class OutputBuffer
        {
            private readonly byte[] _reply;
            private int _at;
            public int Reads;

            public OutputBuffer(string reply) { _reply = Encoding.ASCII.GetBytes(reply); }

            public int Left => _reply.Length - _at;

            public byte[] Read(int count, out bool more)
            {
                Reads++;
                int n = System.Math.Min(count, Left);
                byte[] chunk = _reply.Skip(_at).Take(n).ToArray();
                _at += n;
                more = Left > 0;   // stopped because the buffer filled, not at the end of the reply
                return chunk;
            }
        }

        private static string Trace(int points) =>
            string.Join(",", Enumerable.Range(0, points).Select(i =>
                (-74.2 - i * 0.01).ToString("0.00E+000", System.Globalization.CultureInfo.InvariantCulture))) + "\n";

        [Fact]
        public void AShortReply_IsOneRead()
        {
            var buffer = new OutputBuffer("+0,\"No error\"\n");
            byte[] reply = ReplyReader.ReadToEnd(buffer.Read);

            Assert.Equal("+0,\"No error\"\n", Encoding.ASCII.GetString(reply));
            Assert.Equal(1, buffer.Reads);
        }

        [Fact]
        public void AReplyLongerThanOneRead_IsReadWhole_InOrder()
        {
            // The E4406A's 343-point trace, about 5.5 KB, read 1 KB at a time - the size a single read stopped at.
            string trace = Trace(343);
            var buffer = new OutputBuffer(trace);

            byte[] reply = ReplyReader.ReadToEnd(buffer.Read, chunkBytes: 1024);

            Assert.Equal(trace, Encoding.ASCII.GetString(reply));
            Assert.Equal(343, Encoding.ASCII.GetString(reply).Trim().Split(',').Length);
            Assert.True(buffer.Reads > 1);
            Assert.Equal(0, buffer.Left);   // nothing left for the next command to trip over
        }

        [Fact]
        public void AReplyEndingExactlyOnAChunkBoundary_StopsAtItsEnd()
        {
            var buffer = new OutputBuffer(new string('x', 2048));
            byte[] reply = ReplyReader.ReadToEnd(buffer.Read, chunkBytes: 1024);

            Assert.Equal(2048, reply.Length);
            Assert.Equal(2, buffer.Reads);
        }

        [Fact]
        public void AReplyTooLongToKeep_IsAnError_AndNothingIsLeftOnTheInstrument()
        {
            var buffer = new OutputBuffer(new string('x', 7000));

            var ex = Assert.Throws<ReplyTooLongException>(() => ReplyReader.ReadToEnd(buffer.Read, maxBytes: 4096, chunkBytes: 1024));

            Assert.Equal(4096, ex.Kept);
            Assert.Equal(7000 - 4096, ex.Discarded);
            Assert.Equal(0, buffer.Left);
            Assert.Contains("nothing is left waiting", ex.Message);
        }

        [Fact]
        public void AReplyWhoseLastChunkCrossesTheLimit_IsStillTooLong()
        {
            // Copilot on #150: a 7,000-byte reply read in one 8 KB chunk ends there (no more to read), but it is
            // still over a 4,096-byte limit and must not be returned whole.
            var buffer = new OutputBuffer(new string('x', 7000));

            var ex = Assert.Throws<ReplyTooLongException>(() => ReplyReader.ReadToEnd(buffer.Read, maxBytes: 4096, chunkBytes: 8192));

            Assert.Equal(4096, ex.Kept);
            Assert.Equal(7000 - 4096, ex.Discarded);
            Assert.Equal(0, buffer.Left);
        }

        [Fact]
        public void AReplyExactlyAtTheLimit_IsKept()
        {
            var buffer = new OutputBuffer(new string('x', 4096));
            Assert.Equal(4096, ReplyReader.ReadToEnd(buffer.Read, maxBytes: 4096, chunkBytes: 1024).Length);
        }

        [Fact]
        public void AReplyTooLongEvenToDiscard_SaysTheInstrumentMayNeedAClear()
        {
            var buffer = new OutputBuffer(new string('x', 20000));

            var ex = Assert.Throws<ReplyTooLongException>(() => ReplyReader.ReadToEnd(buffer.Read, maxBytes: 4096, chunkBytes: 1024));

            Assert.True(buffer.Left > 0);
            Assert.Contains("device clear", ex.Message);
        }

        [Fact]
        public void AReadThatReturnsNothing_DoesNotSpin()
        {
            int reads = 0;
            byte[] reply = ReplyReader.ReadToEnd((int count, out bool more) => { reads++; more = true; return new byte[0]; });

            Assert.Empty(reply);
            Assert.Equal(1, reads);
        }
    }
}
