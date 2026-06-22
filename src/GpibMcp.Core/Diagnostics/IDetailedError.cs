namespace GpibMcp.Diagnostics
{
    /// <summary>
    /// An exception that can render a richer, user-facing diagnostic than its one-line
    /// <see cref="System.Exception.Message"/>. The MCP layer renders <see cref="Detail"/> into
    /// the tool's error result when present, so the model can explain the failure to the user.
    /// Declared here (a namespace both the transport and instrument layers already depend on) to
    /// avoid coupling those layers to each other.
    /// </summary>
    public interface IDetailedError
    {
        /// <summary>The full diagnostic text (summary plus any supporting detail).</summary>
        string Detail { get; }
    }
}
