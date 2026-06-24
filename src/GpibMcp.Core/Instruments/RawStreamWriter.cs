using System;

namespace GpibMcp.Instruments
{
    /// <summary>Options for paced raw streaming to a plotter/printer (#77).</summary>
    public sealed class RawWriteOptions
    {
        /// <summary>Max bytes per chunk. A chunk &lt;= the device's input buffer means the bus three-wire
        /// handshake paces it without a single write blocking for the whole (slow) plot. 0 = one write.</summary>
        public int ChunkBytes = 256;

        /// <summary>I/O timeout applied to EACH chunk write (not the whole stream) - it only needs to cover
        /// freeing one chunk's worth of buffer, not drawing the entire plot.</summary>
        public int PerChunkTimeoutMs = InstrumentManager.DefaultTimeoutMs;

        /// <summary>Optional settle between chunks (ms). Usually 0 - the bus handshake already paces - but a
        /// margin helps devices that briefly drop the handshake while moving the pen/head.</summary>
        public int InterChunkDelayMs = 0;
    }

    /// <summary>
    /// Backend-neutral paced raw-byte streamer (#77): splits a payload into bounded chunks and hands each to
    /// a write delegate, optionally pausing between chunks. The whole loop is one server-side operation - no
    /// model roundtrip per chunk - and the per-chunk bound means no single write blocks for the entire plot.
    /// No hardware/VISA dependency, so it is fully unit-testable with a fake write delegate (the same pattern
    /// as <see cref="BatchRunner"/> / <see cref="ScreenCapture"/>).
    /// </summary>
    public static class RawStreamWriter
    {
        /// <summary>
        /// Streams <paramref name="data"/> in chunks of <see cref="RawWriteOptions.ChunkBytes"/> via
        /// <paramref name="writeChunk"/>, calling <paramref name="sleep"/> between chunks when a settle is set.
        /// Returns the number of chunks written.
        /// </summary>
        public static int Stream(byte[] data, RawWriteOptions options, Action<byte[]> writeChunk, Action<int> sleep = null)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (writeChunk == null) throw new ArgumentNullException(nameof(writeChunk));
            options = options ?? new RawWriteOptions();

            int chunkSize = options.ChunkBytes <= 0 ? Math.Max(1, data.Length) : options.ChunkBytes;
            int chunks = 0;
            for (int offset = 0; offset < data.Length; offset += chunkSize)
            {
                int n = Math.Min(chunkSize, data.Length - offset);
                var slice = new byte[n];
                Array.Copy(data, offset, slice, 0, n);
                writeChunk(slice);
                chunks++;

                bool more = offset + n < data.Length;
                if (more && options.InterChunkDelayMs > 0) sleep?.Invoke(options.InterChunkDelayMs);
            }
            return chunks;
        }
    }
}
