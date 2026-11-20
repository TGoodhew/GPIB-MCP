using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace GpibMcp.Setup
{
    /// <summary>One client this changed: which, where, and the <c>gpib</c> entry it had before, if any.</summary>
    public sealed class McpRegistration
    {
        public string Client { get; set; }
        public string Location { get; set; }
        public string Previous { get; set; }
    }

    /// <summary>What removing this server did to one client.</summary>
    public sealed class McpRemoval
    {
        public McpRemoval(McpClient client, McpChange change, bool restored)
        {
            Client = client;
            Change = change;
            Restored = restored;
        }

        public McpClient Client { get; }
        public McpChange Change { get; }
        public bool Restored { get; }
    }

    /// <summary>
    /// The record of what <c>GpibMcp.exe setup mcp</c> changed, so that uninstalling undoes exactly that: an
    /// entry it added is taken out, an entry it replaced is put back as it was, and an entry it found already
    /// in place, or one the user added by hand, is left alone. Kept in
    /// <c>%LOCALAPPDATA%\GpibMcp\mcp-registrations.json</c>.
    /// </summary>
    public sealed class McpRegistrations
    {
        private readonly string _path;
        private readonly List<McpRegistration> _entries;

        private McpRegistrations(string path, List<McpRegistration> entries)
        {
            _path = path;
            _entries = entries;
        }

        public IReadOnlyList<McpRegistration> Entries => _entries;

        public static string PathFor(McpEnvironment environment) =>
            Path.Combine(environment.LocalAppData, "GpibMcp", "mcp-registrations.json");

        public static McpRegistrations Load(McpEnvironment environment)
        {
            string path = PathFor(environment);
            List<McpRegistration> entries = File.Exists(path)
                ? JsonConvert.DeserializeObject<List<McpRegistration>>(File.ReadAllText(path)) ?? new List<McpRegistration>()
                : new List<McpRegistration>();
            return new McpRegistrations(path, entries);
        }

        /// <summary>
        /// Registers with one client, and records it if anything changed. A client already recorded keeps the
        /// entry it had before this first touched it: registering again over this server's own entry must not
        /// make that the one to put back. Nor is an earlier install's entry for the same program worth putting
        /// back - the older install script used the same name and path, and restoring it would leave a client
        /// pointing at a program that uninstalling has just removed.
        /// </summary>
        public McpChange Register(McpClient client, McpServerEntry entry)
        {
            if (client == null) throw new ArgumentNullException(nameof(client));

            // Before taking what is there now: a move that carried this server's entry across is followed,
            // not mistaken for an entry to put back (#178).
            McpRegistration recorded = _entries.FirstOrDefault(e => e.Client == client.Id);
            if (recorded != null) FollowMove(client, recorded, out _);

            string previous = client.Snapshot();
            if (previous != null &&
                string.Equals(client.CommandOf(previous), entry.Command, StringComparison.OrdinalIgnoreCase))
                previous = null;

            McpChange change = client.Register(entry);
            if (change.Changed && _entries.All(e => e.Client != client.Id))
            {
                _entries.Add(new McpRegistration { Client = client.Id, Location = client.Location, Previous = previous });
                Save();
            }
            return change;
        }

        /// <summary>Undoes what was recorded for these clients, and forgets it.</summary>
        public IReadOnlyList<McpRemoval> Remove(IEnumerable<McpClient> clients)
        {
            var removals = new List<McpRemoval>();
            foreach (McpClient client in clients)
            {
                McpRegistration recorded = _entries.FirstOrDefault(e => e.Client == client.Id);
                if (recorded == null) continue;

                McpChange undone;
                McpRegistration now = FollowMove(client, recorded, out undone);
                if (now == null || (undone != null && !undone.Changed && undone.Note != null))
                {
                    // The entry was only in the file the client no longer reads, and is out of it now; or it
                    // couldn't be taken out of there, and the record stays as it was.
                    removals.Add(new McpRemoval(client, undone ?? new McpChange(false, null), recorded.Previous != null));
                    continue;
                }

                McpChange change = now.Previous == null ? client.Unregister() : client.Restore(now.Previous);
                if (undone != null && undone.Changed && !change.Changed && change.Note == null) change = undone;
                removals.Add(new McpRemoval(client, change, now.Previous != null));
                if (change.Changed || change.Note == null) _entries.Remove(now);
            }

            Save();
            return removals;
        }

        /// <summary>
        /// The client at the location an earlier install recorded, when that is no longer where the client keeps
        /// its settings; otherwise null.
        /// </summary>
        private static McpClient Moved(McpClient client, McpRegistration recorded) =>
            string.IsNullOrEmpty(recorded.Location) ||
            string.Equals(Normalise(recorded.Location), Normalise(client.Location), StringComparison.OrdinalIgnoreCase)
                ? null
                : client.At(recorded.Location);

        private static string Normalise(string location)
        {
            try { return Path.GetFullPath(location).TrimEnd('\\'); }
            catch (ArgumentException) { return location; }   // not a path: "claude mcp (user scope)"
            catch (NotSupportedException) { return location; }
        }

        /// <summary>
        /// An earlier install registered with a client whose settings have since moved (#175: current Store
        /// builds of Claude Desktop keep them in <c>Claude-Data</c> and no longer read the old file). The entry
        /// is taken out of the old file, as uninstalling would have done. When the client took its settings
        /// with it (#178), this server's entry is in the new file too, so the record follows it there, still
        /// holding the entry from before this server; otherwise the record is forgotten and the next
        /// registration starts afresh. Returns the record as it now stands, or null when forgotten, with what
        /// was done to the old file in <paramref name="undone"/>. When the old file can't be changed,
        /// everything is left as it was, so nothing is lost.
        /// </summary>
        private McpRegistration FollowMove(McpClient client, McpRegistration recorded, out McpChange undone)
        {
            undone = null;
            McpClient old = Moved(client, recorded);
            if (old == null) return recorded;

            string there = old.Snapshot();
            bool carried = there != null && string.Equals(there, client.Snapshot(), StringComparison.Ordinal);

            if (there != null)
            {
                undone = recorded.Previous == null ? old.Unregister() : old.Restore(recorded.Previous);
                if (!undone.Changed && undone.Note != null) return recorded;
            }

            _entries.Remove(recorded);
            McpRegistration now = carried
                ? new McpRegistration { Client = recorded.Client, Location = client.Location, Previous = recorded.Previous }
                : null;
            if (now != null) _entries.Add(now);
            Save();
            return now;
        }

        private void Save()
        {
            if (_entries.Count == 0)
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path));
            File.WriteAllText(_path, JsonConvert.SerializeObject(_entries, Formatting.Indented) + Environment.NewLine);
        }
    }
}
