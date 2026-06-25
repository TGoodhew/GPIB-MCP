using System.Collections.Generic;
using System.Linq;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>#77: the paced raw streamer must chunk a payload to bounded sizes (so no single write blocks
    /// for the whole plot), reassemble byte-for-byte, and pace between chunks - all without losing a byte.</summary>
    public class RawStreamWriterTests
    {
        private static (List<byte[]> chunks, List<int> sleeps, int count) Run(byte[] data, RawWriteOptions opts)
        {
            var chunks = new List<byte[]>();
            var sleeps = new List<int>();
            int count = RawStreamWriter.Stream(data, opts, (c, _) => chunks.Add(c), sleeps.Add);
            return (chunks, sleeps, count);
        }

        [Fact]
        public void Splits_IntoBoundedChunks_AndReassemblesByteForByte()
        {
            byte[] data = Enumerable.Range(0, 1000).Select(i => (byte)(i & 0xFF)).ToArray();   // includes 0x00..0xFF

            var (chunks, _, count) = Run(data, new RawWriteOptions { ChunkBytes = 256 });

            Assert.Equal(4, count);                                  // 256,256,256,232
            Assert.All(chunks, c => Assert.True(c.Length <= 256));
            Assert.Equal(232, chunks.Last().Length);                // remainder, not padded
            Assert.Equal(data, chunks.SelectMany(c => c).ToArray()); // every byte preserved, in order
        }

        [Fact]
        public void PreservesControlBytes_AcrossChunkBoundaries()
        {
            // ETX/SO/SI/NUL straddling a chunk edge must survive intact.
            byte[] data = { (byte)'A', 0x03, 0x0E, 0x0F, 0x00, (byte)'B', (byte)'C' };

            var (chunks, _, _) = Run(data, new RawWriteOptions { ChunkBytes = 3 });

            Assert.Equal(data, chunks.SelectMany(c => c).ToArray());
        }

        [Fact]
        public void ChunkBytesZero_OrAtLeastLength_SendsOneChunk()
        {
            byte[] data = { 1, 2, 3, 4, 5 };
            Assert.Equal(1, Run(data, new RawWriteOptions { ChunkBytes = 0 }).count);
            Assert.Equal(1, Run(data, new RawWriteOptions { ChunkBytes = 999 }).count);
        }

        [Fact]
        public void SettleIsCalled_BetweenChunksOnly_NotAfterTheLast()
        {
            byte[] data = { 1, 2, 3, 4, 5, 6, 7 };   // 3 chunks of 3,3,1

            var (chunks, sleeps, count) = Run(data, new RawWriteOptions { ChunkBytes = 3, InterChunkDelayMs = 20 });

            Assert.Equal(3, count);
            Assert.Equal(new[] { 20, 20 }, sleeps);   // settles = chunks - 1, never after the final chunk
        }

        [Fact]
        public void IsLast_IsTrueOnlyForTheFinalChunk()
        {
            // The manager uses isLast to assert EOI on the last chunk only (#77) - so a mid-stream EOI can't
            // fragment a coordinate across a chunk boundary and make the plotter draw a stray excursion.
            byte[] data = { 1, 2, 3, 4, 5, 6, 7 };   // 3 chunks of 3,3,1
            var flags = new List<bool>();

            RawStreamWriter.Stream(data, new RawWriteOptions { ChunkBytes = 3 }, (_, isLast) => flags.Add(isLast));

            Assert.Equal(new[] { false, false, true }, flags);
        }

        [Fact]
        public void EmptyData_WritesNothing()
        {
            var (chunks, _, count) = Run(new byte[0], new RawWriteOptions { ChunkBytes = 256 });
            Assert.Empty(chunks);
            Assert.Equal(0, count);
        }
    }
}
