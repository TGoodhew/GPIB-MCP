using GpibMcp.Diagnostics;
using NationalInstruments.NI4882;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Thin wrapper over the native NI-488.2 (GPIB) .NET API for callers that want to
    /// address a board/primary/secondary directly rather than via a VISA resource string.
    ///
    /// Most workflows should prefer <see cref="VisaInstrumentManager"/>; this path exists
    /// to exercise NI-488.2 explicitly and to reach boards/addresses without a VISA alias.
    /// </summary>
    public static class Gpib488Helper
    {
        /// <summary>NI-488.2 convention: secondary address 0 means "no secondary address".</summary>
        public const byte NoSecondaryAddress = 0;

        /// <summary>
        /// Opens a transient device handle at board/primary/secondary, writes the command,
        /// and reads the response. The handle is disposed before returning.
        /// </summary>
        public static string Query(int board, byte primaryAddress, byte secondaryAddress, string command)
        {
            using (var device = new Device(board, primaryAddress, secondaryAddress))
            {
                string payload = CommandText.EnsureTerminated(command);
                Log.Debug(Address(board, primaryAddress, secondaryAddress) + " <- " + CommandText.ForLog(payload));
                device.Write(payload);
                string response = device.ReadString();
                Log.Debug(Address(board, primaryAddress, secondaryAddress) + " -> " + CommandText.ForLog(response));
                return response;
            }
        }

        /// <summary>Writes a command to a device with no expected response.</summary>
        public static void Write(int board, byte primaryAddress, byte secondaryAddress, string command)
        {
            using (var device = new Device(board, primaryAddress, secondaryAddress))
            {
                string payload = CommandText.EnsureTerminated(command);
                Log.Debug(Address(board, primaryAddress, secondaryAddress) + " <- " + CommandText.ForLog(payload));
                device.Write(payload);
            }
        }

        private static string Address(int board, byte primary, byte secondary) =>
            "NI-488.2 GPIB" + board + "::" + primary + (secondary == NoSecondaryAddress ? "" : "::" + secondary);
    }
}
