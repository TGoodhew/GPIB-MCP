using System;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Helpers for preparing instrument command strings and rendering them for logs.
    /// Shared by the VISA and NI-488.2 paths so termination behaviour stays consistent.
    /// </summary>
    internal static class CommandText
    {
        /// <summary>Line terminator appended to commands that do not already carry one.</summary>
        public const string LineTerminator = "\n";

        /// <summary>
        /// Returns <paramref name="command"/> guaranteed to end in a single line terminator.
        /// A null or empty command yields just the terminator.
        /// </summary>
        public static string EnsureTerminated(string command)
        {
            if (string.IsNullOrEmpty(command)) return LineTerminator;
            return command.EndsWith(LineTerminator, StringComparison.Ordinal)
                ? command
                : command + LineTerminator;
        }

        /// <summary>Escapes control characters so raw I/O can be logged on a single readable line.</summary>
        public static string ForLog(string value)
        {
            if (string.IsNullOrEmpty(value)) return "\"\"";
            return "\"" + value.Replace("\\", "\\\\").Replace("\r", "\\r").Replace("\n", "\\n") + "\"";
        }
    }
}
