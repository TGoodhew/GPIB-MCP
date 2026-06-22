using System;
using System.Collections.Generic;

namespace GpibMcp.Instruments
{
    /// <summary>Direction of a recorded instrument I/O event.</summary>
    public enum CommandDirection
    {
        /// <summary>A command written to the instrument.</summary>
        Sent,
        /// <summary>A response read back from the instrument.</summary>
        Received
    }

    /// <summary>One recorded instrument I/O event: a command sent or a response received.</summary>
    public sealed class CommandHistoryEntry
    {
        public string Resource { get; }
        public CommandDirection Direction { get; }
        public string Text { get; }
        public DateTime TimestampUtc { get; }

        public CommandHistoryEntry(string resource, CommandDirection direction, string text, DateTime timestampUtc)
        {
            Resource = resource;
            Direction = direction;
            Text = text ?? string.Empty;
            TimestampUtc = timestampUtc;
        }

        /// <summary>
        /// Renders the entry as one readable line, e.g. <c>12:30:45.123  -&gt; "FREQ 1e6\n"</c>
        /// (<c>-&gt;</c> sent, <c>&lt;-</c> received), reusing <see cref="CommandText.ForLog"/> escaping.
        /// </summary>
        public string ToLine()
        {
            string arrow = Direction == CommandDirection.Sent ? "->" : "<-";
            return TimestampUtc.ToLocalTime().ToString("HH:mm:ss.fff") + "  " + arrow + " " + CommandText.ForLog(Text);
        }
    }

    /// <summary>
    /// Bounded, per-resource ring buffer of recent instrument I/O. Used to show the chain of
    /// commands that led up to a failure. Thread-safe; depth defaults to 20 (override with the
    /// <c>GPIB_MCP_HISTORY_DEPTH</c> environment variable). History never crosses resources.
    /// </summary>
    public sealed class CommandHistory
    {
        public const int DefaultDepth = 20;

        private readonly int _depth;
        private readonly object _gate = new object();
        private readonly Dictionary<string, LinkedList<CommandHistoryEntry>> _byResource =
            new Dictionary<string, LinkedList<CommandHistoryEntry>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Creates a history with the given depth, or the env/default depth when <= 0.</summary>
        public CommandHistory(int depth = 0)
        {
            _depth = depth > 0 ? depth : ResolveDepth();
        }

        private static int ResolveDepth()
        {
            string raw = Environment.GetEnvironmentVariable("GPIB_MCP_HISTORY_DEPTH");
            int parsed;
            if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw.Trim(), out parsed) && parsed > 0) return parsed;
            return DefaultDepth;
        }

        /// <summary>Records one I/O event for a resource, evicting the oldest beyond the depth.</summary>
        public void Record(string resource, CommandDirection direction, string text)
        {
            if (string.IsNullOrEmpty(resource)) return;
            var entry = new CommandHistoryEntry(resource, direction, text, DateTime.UtcNow);
            lock (_gate)
            {
                LinkedList<CommandHistoryEntry> list;
                if (!_byResource.TryGetValue(resource, out list))
                {
                    list = new LinkedList<CommandHistoryEntry>();
                    _byResource[resource] = list;
                }
                list.AddLast(entry);
                while (list.Count > _depth) list.RemoveFirst();
            }
        }

        /// <summary>
        /// Returns up to <paramref name="max"/> most-recent entries for a resource (oldest first),
        /// or an empty list if there is no history for it.
        /// </summary>
        public IReadOnlyList<CommandHistoryEntry> Snapshot(string resource, int max = int.MaxValue)
        {
            if (max <= 0) return Array.Empty<CommandHistoryEntry>();
            lock (_gate)
            {
                LinkedList<CommandHistoryEntry> list;
                if (string.IsNullOrEmpty(resource) || !_byResource.TryGetValue(resource, out list))
                    return Array.Empty<CommandHistoryEntry>();

                var all = new List<CommandHistoryEntry>(list);
                if (max < all.Count) all = all.GetRange(all.Count - max, max);
                return all;
            }
        }
    }
}
