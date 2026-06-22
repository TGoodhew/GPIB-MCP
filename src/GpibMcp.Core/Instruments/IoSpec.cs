namespace GpibMcp.Instruments
{
    /// <summary>
    /// Per-call instrument I/O behaviour, normally resolved from the assigned model's
    /// definition (with optional per-call overrides): the timeout, the line terminators to
    /// use, and an optional bounded read length.
    ///
    /// The bounded read is the safeguard for <b>free-running</b> instruments that stream output
    /// continuously and never assert a normal end-of-response (no terminator/EOI). A plain read
    /// would block until the timeout; with <see cref="MaxReadBytes"/> set, the read returns as
    /// soon as that many bytes have arrived, so identity/queries complete promptly. The
    /// per-instrument <see cref="ReadTermChar"/> is the primary lever; the bounded read is the
    /// complementary backstop for instruments with no usable terminator (issue #35).
    /// </summary>
    public sealed class IoSpec
    {
        /// <summary>I/O timeout in milliseconds. 0 or less means the manager's default.</summary>
        public int TimeoutMs { get; set; }

        /// <summary>
        /// Read termination character (e.g. <c>'\n'</c>). When set, the session reads until this
        /// byte (termination character enabled). When null, VISA's default termination is used
        /// (EOI for GPIB) - this preserves the historical behaviour for unconfigured instruments.
        /// </summary>
        public char? ReadTermChar { get; set; }

        /// <summary>Terminator appended to written commands. Null/empty means the default (<c>"\n"</c>).</summary>
        public string WriteTerminator { get; set; }

        /// <summary>
        /// When set (&gt; 0), read AT MOST this many bytes instead of reading to termination/EOI.
        /// If the instrument streams that many bytes they are returned immediately; if it falls
        /// silent first, the partial data received before the timeout is returned (never throws on
        /// the timeout). Null means an ordinary terminator/EOI-bounded read.
        /// </summary>
        public int? MaxReadBytes { get; set; }

        public IoSpec() { }

        public IoSpec(int timeoutMs) { TimeoutMs = timeoutMs; }
    }
}
