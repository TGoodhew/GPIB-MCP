using System;
using System.Globalization;

namespace GpibMcp.Diagnostics
{
    /// <summary>Severity levels, ordered most-severe (0) to most-verbose.</summary>
    public enum LogLevel
    {
        Error = 0,
        Warn = 1,
        Info = 2,
        Debug = 3
    }

    /// <summary>
    /// Lightweight, thread-safe leveled logger that writes to standard error only.
    ///
    /// stdout is reserved exclusively for MCP JSON-RPC traffic, so every diagnostic
    /// MUST go to stderr. The minimum level is read once at startup from the
    /// <c>GPIB_MCP_LOG_LEVEL</c> environment variable (Error|Warn|Info|Debug) and
    /// defaults to <see cref="LogLevel.Info"/>. Set it to <c>Debug</c> to trace
    /// protocol frames and raw instrument I/O.
    /// </summary>
    public static class Log
    {
        private const string Component = "gpib-mcp";
        private static readonly object Gate = new object();

        /// <summary>Messages at or below (more severe than) this level are emitted.</summary>
        public static LogLevel MinimumLevel { get; set; } = ReadInitialLevel();

        /// <summary>True when a message at <paramref name="level"/> would be written.</summary>
        public static bool IsEnabled(LogLevel level) => level <= MinimumLevel;

        public static void Error(string message) => Write(LogLevel.Error, message);
        public static void Warn(string message) => Write(LogLevel.Warn, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Debug(string message) => Write(LogLevel.Debug, message);

        /// <summary>Logs an error together with the full exception detail.</summary>
        public static void Error(string message, Exception exception) =>
            Write(LogLevel.Error, message + Environment.NewLine + exception);

        /// <summary>Writes a single line to stderr if <paramref name="level"/> is enabled.</summary>
        public static void Write(LogLevel level, string message)
        {
            if (level > MinimumLevel) return;

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-ddTHH:mm:ss.fffZ} [{1}] {2}: {3}",
                DateTime.UtcNow,
                Component,
                level.ToString().ToUpperInvariant(),
                message);

            lock (Gate)
            {
                Console.Error.WriteLine(line);
            }
        }

        private static LogLevel ReadInitialLevel()
        {
            string raw = Environment.GetEnvironmentVariable("GPIB_MCP_LOG_LEVEL");
            LogLevel parsed;
            if (!string.IsNullOrWhiteSpace(raw) &&
                Enum.TryParse(raw.Trim(), ignoreCase: true, result: out parsed) &&
                Enum.IsDefined(typeof(LogLevel), parsed))
            {
                return parsed;
            }
            return LogLevel.Info;
        }
    }
}
