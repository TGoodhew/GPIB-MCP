using System;
using System.IO;
using System.Linq;
using System.Text;

namespace GpibMcp.Instruments
{
    /// <summary>How a binary reply is framed.</summary>
    public enum BlockFormat
    {
        /// <summary>An IEEE 488.2 arbitrary block: <c>#&lt;n&gt;&lt;n digits of length&gt;&lt;bytes&gt;</c>, or <c>#0&lt;bytes&gt;</c> to END.</summary>
        Ieee4882,

        /// <summary>Unframed bytes, read until the instrument ends its message.</summary>
        Raw
    }

    /// <summary>A binary reply: the payload, and the header that framed it.</summary>
    public sealed class BinaryBlockResult
    {
        public BinaryBlockResult(byte[] payload, string header, long? declaredLength)
        {
            Payload = payload;
            Header = header;
            DeclaredLength = declaredLength;
        }

        /// <summary>The bytes, without the header or the trailing terminator.</summary>
        public byte[] Payload { get; }

        /// <summary>The framing as received, e.g. "#540200" or "#0"; null for raw.</summary>
        public string Header { get; }

        /// <summary>The length the header declared; null for "#0" or raw.</summary>
        public long? DeclaredLength { get; }
    }

    /// <summary>
    /// Reads a binary reply - an instrument's screen image, a waveform record - as bytes, never as text
    /// (#139). The header is parsed as it arrives, exactly the declared length is read over as many reads as
    /// it takes (binary data can contain 0x0A, so the termination character plays no part), and the
    /// trailing terminator is consumed. A malformed or short block throws <see cref="BinaryBlockException"/>
    /// saying what did arrive.
    /// </summary>
    public static class BinaryBlock
    {
        /// <summary>Bytes asked for per payload read.</summary>
        public const int ChunkBytes = 64 * 1024;

        /// <summary>Largest block accepted (a screen image is well under this).</summary>
        public const long MaxBytes = 64L * 1024 * 1024;

        /// <summary>Most whitespace skipped before the '#'.</summary>
        private const int MaxLeadingWhitespace = 16;

        /// <summary>Most bytes accepted after a definite-length block: its terminator, CR LF at most.</summary>
        private const int MaxTerminatorBytes = 2;

        /// <summary>
        /// Reads one binary reply through <paramref name="read"/> (see <see cref="ReplyReader.ReadChunk"/>).
        /// <paramref name="timedOut"/> says whether the last read stopped because the instrument fell silent
        /// rather than ending its message: a reply cut off that way is an error, never a short payload.
        /// </summary>
        public static BinaryBlockResult Read(ReplyReader.ReadChunk read, BlockFormat format, long maxBytes = MaxBytes,
                                             Func<bool> timedOut = null)
        {
            Func<bool> silent = timedOut ?? (() => false);

            if (format == BlockFormat.Raw)
            {
                byte[] raw = ReplyReader.ReadToEnd(read, maxBytes, ChunkBytes);
                if (silent()) throw Malformed(TimedOutWhy, new MemoryStream(), raw.Length, null, raw);
                return new BinaryBlockResult(raw, null, null);
            }

            var seen = new MemoryStream();   // everything read, for the error report
            bool more = true;

            // '#', after any leading whitespace.
            byte b;
            int skipped = 0;
            do
            {
                b = ReadByte(read, seen, ref more, "the block header", silent);
                if (!IsWhitespace(b)) break;
                if (++skipped > MaxLeadingWhitespace) throw Malformed("no '#' block header", seen, 0, null);
            } while (true);
            if (b != (byte)'#')
                throw Malformed("the reply doesn't start with an IEEE 488.2 block header '#'", seen, 0, null);

            byte digit = ReadByte(read, seen, ref more, "the block header", silent);
            if (digit < (byte)'0' || digit > (byte)'9')
                throw Malformed("the block header '#' isn't followed by a digit", seen, 0, null);
            int n = digit - (byte)'0';

            if (n == 0)
            {
                // Indefinite length: everything to END, less the terminating newline.
                byte[] rest = more ? ReplyReader.ReadToEnd(read, maxBytes, ChunkBytes) : Array.Empty<byte>();
                if (silent()) throw Malformed(TimedOutWhy, seen, rest.Length, null, rest);
                int end = rest.Length;
                if (end > 0 && rest[end - 1] == (byte)'\n') end--;
                if (end > 0 && rest[end - 1] == (byte)'\r') end--;
                return new BinaryBlockResult(rest.Take(end).ToArray(), "#0", null);
            }

            long length = 0;
            for (int i = 0; i < n; i++)
            {
                byte d = ReadByte(read, seen, ref more, "the block length", silent);
                if (d < (byte)'0' || d > (byte)'9')
                    throw Malformed("the block length isn't " + n + " digits", seen, 0, null);
                length = length * 10 + (d - (byte)'0');
            }
            string header = "#" + n + length.ToString().PadLeft(n, '0');
            if (length > maxBytes)
                throw Malformed("the block declares " + length + " bytes, more than the " + maxBytes + " accepted", seen, 0, length);

            var payload = new byte[length];
            long got = 0;
            while (got < length)
            {
                if (!more) throw Malformed(silent() ? TimedOutWhy : "the instrument ended its message early", seen, got, length, payload);
                byte[] chunk = read((int)Math.Min(ChunkBytes, length - got), out more) ?? Array.Empty<byte>();
                if (chunk.Length == 0) throw Malformed(silent() ? TimedOutWhy : "the read returned nothing", seen, got, length, payload);
                Array.Copy(chunk, 0, payload, got, Math.Min(chunk.Length, length - got));
                got += chunk.Length;
            }

            // The terminator after the block (LF, or CR LF, usually with END), so nothing is left for the next
            // command. Anything else means the declared length was wrong: reported, and the caller drains the
            // rest. A timeout here is accepted: the whole declared payload has arrived.
            int after = 0;
            while (more)
            {
                byte[] tail = read(MaxTerminatorBytes + 1 - after, out more) ?? Array.Empty<byte>();
                if (tail.Length == 0) break;
                if (tail.Any(x => x != (byte)'\r' && x != (byte)'\n') || (after += tail.Length) > MaxTerminatorBytes)
                    throw Malformed("more data followed the " + length + " bytes the header declared (" + Hex(tail.Take(16).ToArray()) +
                                    "), so the declared length is wrong", seen, got, length, payload);
            }

            return new BinaryBlockResult(payload, header, length);
        }

        private const string TimedOutWhy = "the read timed out before the instrument ended its message, so the reply is incomplete";

        private static byte ReadByte(ReplyReader.ReadChunk read, MemoryStream seen, ref bool more, string what, Func<bool> silent)
        {
            if (!more) throw Malformed(silent() ? TimedOutWhy : "the reply ended inside " + what, seen, 0, null);
            byte[] one = read(1, out more) ?? Array.Empty<byte>();
            if (one.Length == 0) throw Malformed(silent() ? TimedOutWhy : "the reply ended inside " + what, seen, 0, null);
            seen.WriteByte(one[0]);
            return one[0];
        }

        private static bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';

        private static BinaryBlockException Malformed(string why, MemoryStream seen, long got, long? expected, byte[] payload = null)
        {
            byte[] head = seen.ToArray();
            byte[] first = payload != null && got > 0 ? payload.Take((int)Math.Min(16, got)).ToArray() : head.Take(16).ToArray();
            return new BinaryBlockException(why, Printable(head), Hex(first), got, expected);
        }

        /// <summary>Bytes as hex, "47 49 46 38".</summary>
        public static string Hex(byte[] bytes) => string.Join(" ", (bytes ?? new byte[0]).Select(x => x.ToString("X2")));

        private static string Printable(byte[] bytes) =>
            new string(bytes.Select(x => x >= 0x20 && x < 0x7F ? (char)x : '.').ToArray());
    }

    /// <summary>A binary reply that wasn't the block it should have been, with what arrived.</summary>
    public sealed class BinaryBlockException : IOException
    {
        public BinaryBlockException(string why, string header, string firstBytesHex, long bytesRead, long? bytesExpected)
            : base("Binary block read failed: " + why + ". Header received: '" + header + "'; first bytes: " +
                   (firstBytesHex.Length > 0 ? firstBytesHex : "(none)") + "; payload bytes read: " + bytesRead +
                   (bytesExpected.HasValue ? " of " + bytesExpected.Value + " expected" : "") + ".")
        {
            Header = header;
            FirstBytesHex = firstBytesHex;
            BytesRead = bytesRead;
            BytesExpected = bytesExpected;
        }

        public string Header { get; }
        public string FirstBytesHex { get; }
        public long BytesRead { get; }
        public long? BytesExpected { get; }
    }
}
