// -----------------------------------------------------------------------------
// Tests for PclRasterRepair (#82): re-framing PCL raster rows shifted by a GPIB
// read drop-out so each ESC*b<n>W is followed by exactly <n> payload bytes.
// -----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Hpgl.Rendering;
using Xunit;

namespace Hpgl.Rendering.Tests
{
    public class PclRasterRepairTests
    {
        private static readonly byte[] Esc = { 0x1B };

        /// <summary>A header (ESC= ESC*rA) then <paramref name="rows"/> raster rows of ESC*b&lt;rowLen&gt;W + rowLen
        /// data bytes, then a trailer (ESC*rB). Each row's data byte is its row index so corruption is visible.</summary>
        private static byte[] Stream(int rows, int rowLen)
        {
            var b = new List<byte>();
            b.AddRange(Cmd("=")); b.AddRange(Cmd("*rA"));
            for (int r = 0; r < rows; r++)
            {
                b.AddRange(Cmd("*b" + rowLen + "W"));
                for (int k = 0; k < rowLen; k++) b.Add((byte)(r & 0xFF));
            }
            b.AddRange(Cmd("*rB"));
            return b.ToArray();
        }

        private static IEnumerable<byte> Cmd(string s)
        {
            yield return 0x1B;
            foreach (char c in s) yield return (byte)c;
        }

        /// <summary>Walks the stream and asserts every ESC*b&lt;n&gt;W row carries exactly n payload bytes before
        /// the next ESC - the invariant a printer relies on to stay in sync.</summary>
        private static void AssertWellFramed(byte[] d)
        {
            int p = 0;
            while (p < d.Length)
            {
                if (d[p] == 0x1B && p + 2 < d.Length && d[p + 1] == (byte)'*' && d[p + 2] == (byte)'b')
                {
                    int j = p + 3, num = 0;
                    while (j < d.Length && d[j] >= '0' && d[j] <= '9') { num = num * 10 + (d[j] - '0'); j++; }
                    if (j < d.Length && d[j] == (byte)'W')
                    {
                        int dataStart = j + 1, end = dataStart + num;
                        Assert.True(end <= d.Length, "row payload runs past end of stream");
                        if (end < d.Length)
                            Assert.Equal(0x1B, d[end]);   // exactly n bytes, then the next command's ESC
                        p = end;
                        continue;
                    }
                }
                p++;
            }
        }

        [Fact]
        public void Repair_LeavesAWellFramedStreamUntouched()
        {
            var clean = Stream(rows: 8, rowLen: 80);
            var fixedBytes = PclRasterRepair.Repair(clean, out int reframed);

            Assert.Equal(0, reframed);
            Assert.Same(clean, fixedBytes);               // unchanged streams return the same reference
        }

        [Fact]
        public void Repair_PadsAShortRowBackToItsDeclaredLength()
        {
            // Drop one data byte from the middle of row 4 - the exact GPIB read-seam defect from #82.
            var clean = Stream(rows: 8, rowLen: 80);
            int row4Cmd = IndexOfNthRow(clean, 4);
            int dropAt = row4Cmd + Cmd("*b80W").Count() + 40;   // a data byte inside row 4
            var corrupt = clean.Take(dropAt).Concat(clean.Skip(dropAt + 1)).ToArray();

            Assert.Equal(clean.Length - 1, corrupt.Length);

            var fixedBytes = PclRasterRepair.Repair(corrupt, out int reframed);

            Assert.Equal(1, reframed);
            Assert.Equal(clean.Length, fixedBytes.Length);     // the dropped byte is restored (as a zero pad)
            AssertWellFramed(fixedBytes);
        }

        [Fact]
        public void Repair_TrimsALongRowBackToItsDeclaredLength()
        {
            // An inserted stray byte makes row 3 one byte long.
            var clean = Stream(rows: 8, rowLen: 80);
            int row3Cmd = IndexOfNthRow(clean, 3);
            int insertAt = row3Cmd + Cmd("*b80W").Count() + 10;
            var corrupt = clean.Take(insertAt).Concat(new byte[] { 0xAA }).Concat(clean.Skip(insertAt)).ToArray();

            var fixedBytes = PclRasterRepair.Repair(corrupt, out int reframed);

            Assert.Equal(1, reframed);
            Assert.Equal(clean.Length, fixedBytes.Length);     // surplus byte dropped, framing restored
            AssertWellFramed(fixedBytes);
        }

        [Fact]
        public void Repair_PreservesRowDataThatLegitimatelyContainsEsc()
        {
            // Uncompressed raster payload can be any 8-bit value, including 0x1B. A clean row whose data holds
            // a 0x1B must NOT be mistaken for a command boundary - the stream is already well framed.
            var b = new List<byte>();
            b.AddRange(Cmd("*rA"));
            b.AddRange(Cmd("*b4W")); b.AddRange(new byte[] { 0x1B, 0x00, 0x1B, 0xFF });  // ESC inside data
            b.AddRange(Cmd("*b4W")); b.AddRange(new byte[] { 0x12, 0x34, 0x56, 0x78 });
            b.AddRange(Cmd("*rB"));
            var clean = b.ToArray();

            var fixedBytes = PclRasterRepair.Repair(clean, out int reframed);

            Assert.Equal(0, reframed);
            Assert.Same(clean, fixedBytes);
        }

        [Fact]
        public void Repair_HealsTheDownstreamMisframeFromASingleDrop()
        {
            // End to end: after the dropped byte, the corrupt stream's later rows are mis-aligned; the repaired
            // stream must put every row back on its boundary so no row prints its command as literal text.
            var clean = Stream(rows: 12, rowLen: 64);
            int row5Cmd = IndexOfNthRow(clean, 5);
            int dropAt = row5Cmd + Cmd("*b64W").Count() + 20;
            var corrupt = clean.Take(dropAt).Concat(clean.Skip(dropAt + 1)).ToArray();

            var fixedBytes = PclRasterRepair.Repair(corrupt, out int reframed);

            Assert.Equal(1, reframed);
            AssertWellFramed(fixedBytes);
        }

        /// <summary>Byte index of the n-th (0-based) ESC*b...W row command in a stream built by <see cref="Stream"/>.</summary>
        private static int IndexOfNthRow(byte[] d, int n)
        {
            int seen = 0;
            for (int i = 0; i + 2 < d.Length; i++)
            {
                if (d[i] == 0x1B && d[i + 1] == (byte)'*' && d[i + 2] == (byte)'b')
                {
                    if (seen == n) return i;
                    seen++;
                }
            }
            return -1;
        }
    }
}
