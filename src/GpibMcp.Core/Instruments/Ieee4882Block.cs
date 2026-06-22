using System;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Parses an IEEE 488.2 arbitrary block response - the framing instruments use to return binary
    /// payloads such as a screen image or waveform record:
    ///   definite length:   <c>#&lt;n&gt;&lt;len&gt;&lt;data...&gt;</c>  (n digits give the byte count, then that many bytes)
    ///   indefinite length: <c>#0&lt;data...&gt;</c>                 (data runs to the message end / EOI)
    /// </summary>
    public static class Ieee4882Block
    {
        /// <summary>
        /// Extracts the binary payload from an IEEE 488.2 block response, stripping the
        /// <c>#&lt;n&gt;&lt;len&gt;</c> header (and any leading whitespace before it). A definite-length block
        /// returns exactly its declared length; an indefinite block (<c>#0</c>) returns everything after the
        /// header with a single trailing newline trimmed. If no valid block header is present, the input is
        /// returned unchanged (some instruments emit the raw image with no framing).
        /// </summary>
        public static byte[] ExtractDefiniteLength(byte[] response)
        {
            if (response == null || response.Length == 0) return Array.Empty<byte>();

            int i = 0;
            while (i < response.Length && IsWhitespace(response[i])) i++;     // skip leading CR/LF/space/tab
            if (i >= response.Length || response[i] != (byte)'#') return response; // no block header

            int hash = i;
            i++;                                                              // past '#'
            if (i >= response.Length || !IsDigit(response[i])) return response;
            int n = response[i] - (byte)'0';
            i++;

            if (n == 0)
            {
                // Indefinite-length block: payload runs to the end; trim one trailing newline if present.
                int end = response.Length;
                if (end > i && response[end - 1] == (byte)'\n') end--;
                if (end > i && response[end - 1] == (byte)'\r') end--;
                return Slice(response, i, end - i);
            }

            if (i + n > response.Length) return response;                     // truncated length field
            long len = 0;
            for (int k = 0; k < n; k++)
            {
                if (!IsDigit(response[i + k])) return response;               // malformed -> treat as raw
                len = len * 10 + (response[i + k] - (byte)'0');
            }
            i += n;

            long available = response.Length - i;
            long take = Math.Min(len, available);                            // never read past what arrived
            if (take <= 0) return Slice(response, hash, response.Length - hash);
            return Slice(response, i, (int)take);
        }

        private static byte[] Slice(byte[] src, int start, int count)
        {
            if (count <= 0) return Array.Empty<byte>();
            var result = new byte[count];
            Array.Copy(src, start, result, 0, count);
            return result;
        }

        private static bool IsDigit(byte b) => b >= (byte)'0' && b <= (byte)'9';
        private static bool IsWhitespace(byte b) => b == (byte)' ' || b == (byte)'\t' || b == (byte)'\r' || b == (byte)'\n';
    }
}
