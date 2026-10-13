using System;
using System.IO;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Reads a whole text reply, however many reads it takes. A transport's single read returns after one
    /// buffer when a reply is longer - NI-VISA's ReadString() stopped at about 1 KB of a 5.5 KB trace - and
    /// the rest stayed in the instrument's output buffer, where the next command met it as -410 "Query
    /// INTERRUPTED" (#141). So the transport reads in chunks until it reports the reply has ended.
    /// </summary>
    public static class ReplyReader
    {
        /// <summary>Bytes asked for per read.</summary>
        public const int ChunkBytes = 64 * 1024;

        /// <summary>Longest text reply kept; anything longer is an error (and its remainder is discarded).</summary>
        public const long MaxReplyBytes = 16L * 1024 * 1024;

        /// <summary>
        /// One read of up to <paramref name="count"/> bytes. <paramref name="more"/> is true when the read stopped
        /// only because it filled - the reply goes on - and false when the reply ended (END/EOI or the
        /// termination character).
        /// </summary>
        public delegate byte[] ReadChunk(int count, out bool more);

        /// <summary>
        /// Reads until the reply ends. A reply longer than <paramref name="maxBytes"/> throws
        /// <see cref="ReplyTooLongException"/> after its remainder has been read and discarded, so the
        /// instrument is left with nothing pending.
        /// </summary>
        public static byte[] ReadToEnd(ReadChunk read, long maxBytes = MaxReplyBytes, int chunkBytes = ChunkBytes)
        {
            using (var reply = new MemoryStream())
            {
                bool more;
                do
                {
                    byte[] chunk = read(chunkBytes, out more) ?? Array.Empty<byte>();
                    reply.Write(chunk, 0, chunk.Length);
                    if (chunk.Length == 0) break;   // nothing arrived: never spin

                    if (more && reply.Length >= maxBytes)
                    {
                        long discarded = 0;
                        while (more && discarded < maxBytes)
                        {
                            int n = (read(chunkBytes, out more) ?? Array.Empty<byte>()).Length;
                            if (n == 0) break;
                            discarded += n;
                        }
                        throw new ReplyTooLongException(reply.Length, discarded, more);
                    }
                } while (more);
                return reply.ToArray();
            }
        }
    }

    /// <summary>A text reply longer than <see cref="ReplyReader.MaxReplyBytes"/>.</summary>
    public sealed class ReplyTooLongException : IOException
    {
        public ReplyTooLongException(long kept, long discarded, bool stillMore)
            : base("The reply was longer than " + (kept / (1024 * 1024)) + " MB, so it was not returned. The rest (" +
                   discarded + " more bytes) was read and discarded" +
                   (stillMore
                       ? ", but still more remained: the instrument may need a device clear before its next command " +
                         "(on HP 8560-series analyzers a device clear also presets the instrument)."
                       : ", so nothing is left waiting on the instrument.") +
                   " Ask for less data, or read it as a binary block.")
        {
            Kept = kept;
            Discarded = discarded;
        }

        public long Kept { get; }
        public long Discarded { get; }
    }
}
