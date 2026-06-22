using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Low-level wire access for a screen capture: send a command/reply, and read a chunk
    /// (returning whatever was received, with <see cref="CaptureRead.TimedOut"/> set when the
    /// per-read timeout fired - the signal that the instrument has paused/finished). Abstracted
    /// so the capture loop is unit-testable with a scripted fake, no hardware required.
    /// </summary>
    internal interface ICaptureChannel
    {
        void Send(string text);
        CaptureRead Read();
    }

    internal struct CaptureRead
    {
        public byte[] Data;
        public bool TimedOut;
        public CaptureRead(byte[] data, bool timedOut) { Data = data; TimedOut = timedOut; }
    }

    /// <summary>Why a capture finished.</summary>
    public enum CaptureCompletion
    {
        /// <summary>Pen-up (SP;/SP0;) seen near the tail - the instrument finished plotting.</summary>
        PenUp,
        /// <summary>No new data for the inactivity interval.</summary>
        Inactivity,
        /// <summary>Overall backstop timeout reached.</summary>
        Backstop,
        /// <summary>PCL end-raster / printer-reset marker seen near the tail - the print dump finished.</summary>
        EndMarker
    }

    /// <summary>What wire protocol the capture is reading.</summary>
    public enum CaptureMode
    {
        /// <summary>HP-GL "plot": emulate a 7470A plotter, answering OS/OE queries, complete on pen-up.</summary>
        PlotterEmulation,
        /// <summary>PCL "print": read the raster the instrument streams, complete on the PCL end marker / inactivity.</summary>
        PrinterStream
    }

    /// <summary>Result of a screen capture: the raw HP-GL plus metadata.</summary>
    public sealed class CaptureResult
    {
        public string Hpgl { get; }
        public int ByteCount { get; }
        public long ElapsedMs { get; }
        public CaptureCompletion Completion { get; }

        public CaptureResult(string hpgl, int byteCount, long elapsedMs, CaptureCompletion completion)
        {
            Hpgl = hpgl;
            ByteCount = byteCount;
            ElapsedMs = elapsedMs;
            Completion = completion;
        }
    }

    /// <summary>Tunables for the plotter-emulation capture (KE5FX-derived defaults).</summary>
    public sealed class CaptureOptions
    {
        public int PerReadTimeoutMs { get; set; } = 1000;
        public int InactivityTimeoutMs { get; set; } = 3500;
        public int OverallTimeoutMs { get; set; } = 30000;
        public int MinPlotBytes { get; set; } = 128;
        public int ReadChunkSize { get; set; } = 4096;

        /// <summary>HP 7470A status sequence: 24 on the first OS, 16 thereafter.</summary>
        public int OsFirst { get; set; } = 24;
        public int OsSubsequent { get; set; } = 16;

        /// <summary>Wire protocol to read: HP-GL plotter emulation (default) or a PCL printer stream.</summary>
        public CaptureMode Mode { get; set; } = CaptureMode.PlotterEmulation;

        /// <summary>Terminator appended to handshake replies.</summary>
        public string ReplyTerminator { get; set; } = "\r\n";

        // Optional replies for the other output queries. Null = do not answer (KE5FX default).
        public string OeReply { get; set; }
        public string OaReply { get; set; }
        public string OcReply { get; set; }
        public string OfReply { get; set; }
    }

    /// <summary>
    /// Controller-side HP-GL plot capture by emulating an HP 7470A plotter.
    ///
    /// Sends the pre-roll and plot command, then reads the HP-GL the instrument streams while
    /// answering the output-status (OS) queries it pauses on, until a pen-up (SP0) is seen or the
    /// bus goes inactive. This is the C# port of the loop in KE5FX's 7470.cpp (async_read);
    /// original C++ author John Miles, KE5FX - http://www.ke5fx.com/
    /// </summary>
    internal static class ScreenCapture
    {
        public static CaptureResult Run(ICaptureChannel channel, string preRoll, string plotCommand,
                                        CaptureOptions o, Func<long> nowMs)
        {
            if (!string.IsNullOrEmpty(preRoll)) channel.Send(preRoll);
            channel.Send(plotCommand);

            var buffer = new StringBuilder();
            int osState = o.OsFirst;
            long start = nowMs();
            long lastData = start;

            while (true)
            {
                CaptureRead read = channel.Read();

                if (read.Data != null && read.Data.Length > 0)
                {
                    Append(buffer, read.Data);
                    lastData = nowMs();
                }

                if (!read.TimedOut)
                    continue; // data still flowing - keep reading

                // A pause. In plotter emulation the instrument may be waiting for a plotter reply;
                // a PCL printer stream never handshakes, so we only answer queries in plot mode.
                if (o.Mode == CaptureMode.PlotterEmulation)
                {
                    if (TryAnswerQuery(channel, buffer, o, ref osState))
                        continue;

                    if (buffer.Length >= o.MinPlotBytes && PenUpInTail(buffer, o.ReadChunkSize))
                        return Result(buffer, start, nowMs(), CaptureCompletion.PenUp);
                }
                else if (buffer.Length >= o.MinPlotBytes && EndOfPrintInTail(buffer, o.ReadChunkSize))
                {
                    return Result(buffer, start, nowMs(), CaptureCompletion.EndMarker);
                }

                if (buffer.Length >= o.MinPlotBytes && (nowMs() - lastData) >= o.InactivityTimeoutMs)
                    return Result(buffer, start, nowMs(), CaptureCompletion.Inactivity);

                if ((nowMs() - start) >= o.OverallTimeoutMs)
                    return Result(buffer, start, nowMs(), CaptureCompletion.Backstop);

                // Otherwise: sub-threshold buffer (preamble noise) or not idle long enough.
                // Keep waiting - bounded by the overall backstop above.
            }
        }

        private static bool TryAnswerQuery(ICaptureChannel channel, StringBuilder buffer,
                                           CaptureOptions o, ref int osState)
        {
            // Find the last meaningful (non-space, non-';', non-CR/LF) character.
            int end = buffer.Length - 1;
            while (end >= 0 && (buffer[end] == ' ' || buffer[end] == ';' ||
                                buffer[end] == '\r' || buffer[end] == '\n'))
                end--;
            if (end < 1) return false;

            string op = string.Concat(char.ToUpperInvariant(buffer[end - 1]),
                                      char.ToUpperInvariant(buffer[end]));

            string reply = null;
            bool isOs = false;
            if (op == "OS") { reply = osState.ToString(CultureInfo.InvariantCulture); isOs = true; }
            else if (op == "OE") reply = o.OeReply;
            else if (op == "OA") reply = o.OaReply;
            else if (op == "OC") reply = o.OcReply;
            else if (op == "OF") reply = o.OfReply;

            if (reply == null) return false;

            channel.Send(reply + o.ReplyTerminator);
            if (isOs) osState = o.OsSubsequent;

            // Strip the answered opcode (and trailing junk) so a later timeout doesn't re-answer it.
            buffer.Length = end - 1;
            return true;
        }

        private static bool PenUpInTail(StringBuilder buffer, int window)
        {
            int from = Math.Max(0, buffer.Length - window);
            string tail = buffer.ToString(from, buffer.Length - from);
            return tail.IndexOf("SP;", StringComparison.Ordinal) >= 0
                || tail.IndexOf("SP0;", StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// Detects the end of a PCL print dump near the tail: an End Raster Graphics (ESC*rB / ESC*rC)
        /// or a printer reset (ESC E). Bytes are held as Latin-1 chars, so ESC is char 0x1B.
        /// </summary>
        private static bool EndOfPrintInTail(StringBuilder buffer, int window)
        {
            const char esc = (char)0x1B;
            int from = Math.Max(0, buffer.Length - window);
            string tail = buffer.ToString(from, buffer.Length - from);
            return tail.IndexOf(esc + "*rB", StringComparison.Ordinal) >= 0
                || tail.IndexOf(esc + "*rC", StringComparison.Ordinal) >= 0
                || tail.IndexOf(esc + "E", StringComparison.Ordinal) >= 0;
        }

        private static void Append(StringBuilder buffer, byte[] data)
        {
            // HP-GL is ASCII/Latin-1; map bytes directly to chars.
            foreach (byte b in data) buffer.Append((char)b);
        }

        private static CaptureResult Result(StringBuilder buffer, long start, long end, CaptureCompletion reason)
            => new CaptureResult(buffer.ToString(), buffer.Length, end - start, reason);
    }
}
