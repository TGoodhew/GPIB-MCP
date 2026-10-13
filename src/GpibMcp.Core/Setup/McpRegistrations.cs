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

                McpChange change = recorded.Previous == null ? client.Unregister() : client.Restore(recorded.Previous);
                removals.Add(new McpRemoval(client, change, recorded.Previous != null));
                if (change.Changed || change.Note == null) _entries.Remove(recorded);
            }

            Save();
            return removals;
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
