using System.Collections.Generic;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Abstraction over the instrument I/O layer consumed by the MCP tools.
    /// <see cref="VisaInstrumentManager"/> implements it for real hardware; tests can
    /// substitute a fake so tool behaviour is verifiable without instruments attached.
    /// </summary>
    public interface IInstrumentManager
    {
        /// <summary>Discovers connected VISA resources matching <paramref name="filter"/>.</summary>
        IList<string> ListResources(string filter);

        /// <summary>Writes a command and reads the instrument's response.</summary>
        string Query(string resource, string command, int timeoutMs);

        /// <summary>Writes a command with no expected response.</summary>
        void Write(string resource, string command, int timeoutMs);

        /// <summary>Reads a pending response from a previously written command.</summary>
        string Read(string resource, int timeoutMs);

        /// <summary>Sends an IEEE 488.2 device clear.</summary>
        void Clear(string resource, int timeoutMs);

        /// <summary>Returns the resource strings of all sessions currently held open.</summary>
        IList<string> ListOpen();

        /// <summary>Closes a held-open session; returns false if none was open for the resource.</summary>
        bool Close(string resource);

        /// <summary>
        /// Returns up to <paramref name="max"/> of the most recent commands sent to / responses
        /// received from a resource (oldest first) - the chain leading up to now or to an error.
        /// </summary>
        IReadOnlyList<CommandHistoryEntry> RecentCommands(string resource, int max);

        /// <summary>
        /// Captures an HP-GL plot from the instrument (plotter emulation): sends pre-roll + plot
        /// command, answers the handshake, and returns the raw HP-GL. Leaves the bus usable.
        /// </summary>
        CaptureResult CaptureScreen(string resource, string preRoll, string plotCommand, CaptureOptions options);
    }
}
