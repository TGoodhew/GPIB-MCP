using System;
using System.Collections.Generic;
using System.Text;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>The instrument operation that failed.</summary>
    public enum GpibOperation { Query, Write, Read, Clear, Capture, Open }

    /// <summary>
    /// A GPIB/VISA I/O failure, enriched with everything needed to explain it: the operation +
    /// resource + command that failed, the decoded VISA status, and the recent command chain that
    /// led up to it. <see cref="Detail"/> is what the MCP layer surfaces to the client (via
    /// <see cref="IDetailedError"/>); <see cref="Exception.Message"/> stays a concise one-liner.
    /// </summary>
    public sealed class GpibOperationException : Exception, IDetailedError
    {
        public GpibOperation Operation { get; }
        public string Resource { get; }
        public string Command { get; }
        public string VisaStatusName { get; }
        public string VisaStatusMeaning { get; }
        public IReadOnlyList<CommandHistoryEntry> History { get; }

        private GpibOperationException(string message, Exception inner, GpibOperation op, string resource,
            string command, string statusName, string statusMeaning, IReadOnlyList<CommandHistoryEntry> history)
            : base(message, inner)
        {
            Operation = op;
            Resource = resource;
            Command = command;
            VisaStatusName = statusName;
            VisaStatusMeaning = statusMeaning;
            History = history ?? Array.Empty<CommandHistoryEntry>();
        }

        /// <summary>Builds a <see cref="GpibOperationException"/> from a raw failure + the command chain.</summary>
        internal static GpibOperationException For(GpibOperation op, string resource, string command,
            Exception inner, IReadOnlyList<CommandHistoryEntry> history)
        {
            VisaErrorInfo.Info info = VisaErrorInfo.Describe(inner);
            string summary = BuildSummary(op, resource, command, info, inner);
            return new GpibOperationException(summary, inner, op, resource, command,
                info.HasName ? info.Name : null, info.Meaning, history);
        }

        private static string BuildSummary(GpibOperation op, string resource, string command,
            VisaErrorInfo.Info info, Exception inner)
        {
            var sb = new StringBuilder();
            sb.Append(op.ToString().ToLowerInvariant()).Append(" failed on ").Append(resource ?? "(no resource)");
            if (!string.IsNullOrEmpty(command)) sb.Append(" [command: ").Append(command).Append(']');
            sb.Append(": ");
            if (info.HasName) sb.Append(info.Name).Append(" - ").Append(info.Meaning);
            else sb.Append(inner != null ? inner.Message : "unknown error");
            return sb.ToString();
        }

        /// <summary>
        /// The full, user-facing diagnostic: the summary plus the recent command chain (if any),
        /// so the model can show the user exactly what failed and the sequence that led to it.
        /// </summary>
        public string Detail
        {
            get
            {
                var sb = new StringBuilder();
                sb.Append(Message);
                if (History.Count > 0)
                {
                    sb.Append("\n\nRecent command chain for ").Append(Resource)
                      .Append(" (-> sent / <- received):");
                    foreach (CommandHistoryEntry e in History) sb.Append("\n  ").Append(e.ToLine());
                }
                return sb.ToString();
            }
        }
    }
}
