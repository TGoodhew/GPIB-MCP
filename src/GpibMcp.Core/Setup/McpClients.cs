using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Setup
{
    /// <summary>What every client is told: run this program, with the manual folder here (if any).</summary>
    public sealed class McpServerEntry
    {
        /// <summary>The server's name in every client's settings - the name the older install script used.</summary>
        public const string Name = "gpib";

        public const string ManualsVariable = "GPIB_MCP_MANUALS";

        public McpServerEntry(string command, string manuals)
        {
            Command = command;
            Manuals = string.IsNullOrWhiteSpace(manuals) ? null : manuals;
        }

        public string Command { get; }

        /// <summary>The manual folder, or null when manual lookup is off.</summary>
        public string Manuals { get; }
    }

    /// <summary>Where a client stands with this server.</summary>
    public enum McpClientState
    {
        NotInstalled,
        NotRegistered,
        Registered,

        /// <summary>Registered, but launching another program or manual folder than the one asked for.</summary>
        Different,

        /// <summary>Its settings file could not be read, so it is left alone.</summary>
        Unreadable,

        /// <summary>Claude Desktop already runs this server as an extension (.mcpb); a second entry would duplicate it.</summary>
        HasExtension
    }

    /// <summary>What registering or removing did to one client's settings.</summary>
    public sealed class McpChange
    {
        public McpChange(bool changed, string backup, string note = null)
        {
            Changed = changed;
            Backup = backup;
            Note = note;
        }

        public bool Changed { get; }

        /// <summary>The copy of the file from before this server first changed it, when one is kept.</summary>
        public string Backup { get; }

        public string Note { get; }

        public McpChange WithBackup(string backup) => new McpChange(Changed, backup, Note);
    }

    /// <summary>
    /// An application that can launch a local MCP server, and how to tell it about this one. Each knows where
    /// its settings live, whether it is installed, and how to add and remove one entry without disturbing
    /// anything else in the file. The design is ManualForge's (TGoodhew/ManualForge#26).
    /// </summary>
    public abstract class McpClient
    {
        protected McpClient(string id, string displayName)
        {
            Id = id;
            DisplayName = displayName;
        }

        public string Id { get; }
        public string DisplayName { get; }

        /// <summary>The file that holds its servers, or a description of how they are kept.</summary>
        public abstract string Location { get; }

        public abstract bool IsInstalled { get; }

        public abstract McpClientState State(McpServerEntry entry);

        public abstract McpChange Register(McpServerEntry entry);

        public abstract McpChange Unregister();

        /// <summary>
        /// The <c>gpib</c> entry as it stands, in the client's own form, or null when it has none. Taken before
        /// an entry already there is changed, so uninstalling can put it back.
        /// </summary>
        public abstract string Snapshot();

        /// <summary>Puts back an entry <see cref="Snapshot"/> took.</summary>
        public abstract McpChange Restore(string snapshot);

        /// <summary>The command a snapshot launches, so a snapshot of this server's own entry can be recognised.</summary>
        public virtual string CommandOf(string snapshot) => null;

        /// <summary>What to tell the user afterwards, when the client reads its settings only at start.</summary>
        public virtual string RestartNote => null;

        /// <summary>
        /// Copies a settings file aside the first time it is changed: beside it, named for this server, so the
        /// user can see whose it is and put it back. Never overwritten, so it stays the file as it was before.
        /// </summary>
        protected static string Backup(string path)
        {
            if (!File.Exists(path)) return null;

            string backup = BackupPathFor(path);
            if (!File.Exists(backup)) File.Copy(path, backup);
            return backup;
        }

        protected static string BackupPathFor(string path) => path + ".before-gpib-mcp.bak";

        /// <summary>
        /// Once this server's entry is out again, the copy from before has nothing left to restore. Kept only
        /// when it holds comments the edit could not keep, which exist nowhere else.
        /// </summary>
        protected static void DropBackup(string path)
        {
            string backup = BackupPathFor(path);
            try
            {
                if (File.Exists(backup) && !JsonMcpClient.HasComments(File.ReadAllText(backup)))
                    File.Delete(backup);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A client whose servers sit in one JSON object of a settings file: <c>mcpServers</c> for Claude Desktop,
    /// Cursor, Windsurf, Gemini CLI, LM Studio and Claude Code; <c>servers</c>, with a <c>type</c>, for VS Code
    /// and Visual Studio.
    /// </summary>
    public class JsonMcpClient : McpClient
    {
        private static readonly JsonLoadSettings Reading = new JsonLoadSettings
        {
            CommentHandling = CommentHandling.Ignore,
            DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Replace
        };

        private readonly Func<bool> _installed;
        private readonly string _serversKey;
        private readonly bool _typed;
        private readonly string _restartNote;

        public JsonMcpClient(string id, string displayName, string configPath, Func<bool> installed,
                             string serversKey, bool typed, string restartNote = null)
            : base(id, displayName)
        {
            ConfigPath = configPath;
            _installed = installed;
            _serversKey = serversKey;
            _typed = typed;
            _restartNote = restartNote;
        }

        public string ConfigPath { get; }

        public override string Location => ConfigPath;

        public override bool IsInstalled => _installed();

        public override string RestartNote => _restartNote;

        public override McpClientState State(McpServerEntry entry)
        {
            if (!IsInstalled) return McpClientState.NotInstalled;

            JObject root;
            if (!TryRead(out root)) return McpClientState.Unreadable;

            var existing = root?[_serversKey]?[McpServerEntry.Name] as JObject;
            if (existing == null) return McpClientState.NotRegistered;

            return Launches(existing, entry) ? McpClientState.Registered : McpClientState.Different;
        }

        /// <summary>
        /// Whether an entry already starts this server with this manual folder. Clients add their own keys -
        /// Claude Code writes <c>"type": "stdio"</c> and an empty <c>args</c> - so this asks what it would
        /// launch rather than whether it is written the same way.
        /// </summary>
        internal static bool Launches(JObject existing, McpServerEntry entry)
        {
            bool sameCommand = string.Equals(Text(existing["command"]), entry.Command, StringComparison.OrdinalIgnoreCase);
            string manuals = Text(existing["env"]?[McpServerEntry.ManualsVariable]);
            bool sameManuals = string.Equals(string.IsNullOrEmpty(manuals) ? null : manuals, entry.Manuals,
                                             StringComparison.OrdinalIgnoreCase);
            var args = existing["args"] as JArray;
            bool noArguments = existing["args"] == null || existing["args"].Type == JTokenType.Null || (args != null && args.Count == 0);
            return sameCommand && sameManuals && noArguments;
        }

        private static string Text(JToken token) => token != null && token.Type == JTokenType.String ? (string)token : null;

        public override McpChange Register(McpServerEntry entry)
        {
            JObject root;
            if (!TryRead(out root))
                return new McpChange(false, null, "its settings could not be read as JSON, so they were left alone");

            if (root == null) root = new JObject();
            var servers = root[_serversKey] as JObject;
            if (servers == null)
            {
                servers = new JObject();
                root[_serversKey] = servers;
            }

            var existing = servers[McpServerEntry.Name] as JObject;
            if (existing != null && Launches(existing, entry)) return new McpChange(false, null);

            servers[McpServerEntry.Name] = Entry(entry);
            return Write(root);
        }

        public override McpChange Unregister()
        {
            JObject root;
            if (!File.Exists(ConfigPath) || !TryRead(out root)) return new McpChange(false, null);
            var servers = root?[_serversKey] as JObject;
            if (servers == null || servers.Property(McpServerEntry.Name) == null) return new McpChange(false, null);

            servers.Remove(McpServerEntry.Name);

            // A file this created holds nothing else once its entry is out: it goes too.
            if (servers.Count == 0 && root.Count == 1 && !File.Exists(BackupPathFor(ConfigPath)))
            {
                File.Delete(ConfigPath);
                return new McpChange(true, null);
            }

            // An empty servers object this added along with its entry goes with it, so a file that had none
            // before - Claude Code's, say - can match its backup and go back byte for byte.
            if (servers.Count == 0 && !BackupHas(_serversKey)) root.Remove(_serversKey);

            if (PutBackIfUnchanged(root)) return new McpChange(true, null);

            McpChange change = Write(root);
            DropBackup(ConfigPath);
            return change.WithBackup(File.Exists(BackupPathFor(ConfigPath)) ? BackupPathFor(ConfigPath) : null);
        }

        /// <summary>Whether the file as it was before this first changed it had this top-level key.</summary>
        private bool BackupHas(string key)
        {
            try
            {
                string backup = BackupPathFor(ConfigPath);
                return File.Exists(backup) && (JToken.Parse(File.ReadAllText(backup), Reading) as JObject)?.Property(key) != null;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return true;   // when in doubt, keep the key
            }
        }

        public override string Snapshot()
        {
            JObject root;
            if (!TryRead(out root)) return null;
            var existing = root?[_serversKey]?[McpServerEntry.Name] as JObject;
            return existing?.ToString(Formatting.None);
        }

        public override string CommandOf(string snapshot)
        {
            try { return Text(JObject.Parse(snapshot)["command"]); }
            catch (JsonException) { return null; }
        }

        public override McpChange Restore(string snapshot)
        {
            JObject root;
            if (!TryRead(out root))
                return new McpChange(false, null, "its settings could not be read as JSON, so they were left alone");

            if (root == null) root = new JObject();
            var servers = root[_serversKey] as JObject;
            if (servers == null)
            {
                servers = new JObject();
                root[_serversKey] = servers;
            }

            servers[McpServerEntry.Name] = JToken.Parse(snapshot);
            if (PutBackIfUnchanged(root)) return new McpChange(true, null);

            McpChange change = Write(root);
            DropBackup(ConfigPath);
            return change.WithBackup(File.Exists(BackupPathFor(ConfigPath)) ? BackupPathFor(ConfigPath) : null);
        }

        /// <summary>
        /// When taking this server out leaves the settings saying exactly what they said before it went in,
        /// the copy from before goes back byte for byte, with its layout and comments, rather than the same
        /// settings written out again. Anything changed since, by the user or the app, keeps the rewrite.
        /// </summary>
        private bool PutBackIfUnchanged(JObject root)
        {
            string backup = BackupPathFor(ConfigPath);
            try
            {
                if (!File.Exists(backup)) return false;
                var before = JToken.Parse(File.ReadAllText(backup), Reading) as JObject;
                if (before == null || !JToken.DeepEquals(before, root)) return false;

                File.Copy(backup, ConfigPath, overwrite: true);
                File.Delete(backup);
                return true;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>What goes under the server's name.</summary>
        protected JObject Entry(McpServerEntry entry)
        {
            var node = new JObject();
            if (_typed) node["type"] = "stdio";
            node["command"] = entry.Command;
            if (entry.Manuals != null)
                node["env"] = new JObject { [McpServerEntry.ManualsVariable] = entry.Manuals };
            return node;
        }

        private bool TryRead(out JObject root)
        {
            root = null;
            if (!File.Exists(ConfigPath)) return true;

            try
            {
                string text = File.ReadAllText(ConfigPath);
                if (string.IsNullOrWhiteSpace(text)) return true;

                root = JToken.Parse(text, Reading) as JObject;
                return root != null;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                return false;
            }
        }

        private McpChange Write(JObject root)
        {
            bool hadComments = File.Exists(ConfigPath) && HasComments(File.ReadAllText(ConfigPath));
            string backup = Backup(ConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            // UTF-8 without a BOM: some clients refuse a settings file that starts with one.
            File.WriteAllText(ConfigPath, root.ToString(Formatting.Indented) + Environment.NewLine);
            return new McpChange(true, backup, hadComments ? "comments in the file were not kept; the backup has them" : null);
        }

        /// <summary>Whether a settings file holds comments, which rewriting it as plain JSON would lose.</summary>
        internal static bool HasComments(string text)
        {
            try
            {
                using (var reader = new JsonTextReader(new StringReader(text)))
                {
                    while (reader.Read())
                        if (reader.TokenType == JsonToken.Comment) return true;
                }
            }
            catch (JsonException) { }
            return false;
        }
    }

    /// <summary>
    /// Claude Desktop. Two things set it apart: the Microsoft Store build keeps its settings in its package's
    /// virtualised AppData, and a user who installed the .mcpb extension already has this server - a config
    /// entry as well would start it twice, both fighting over the GPIB bus.
    /// </summary>
    public sealed class ClaudeDesktopClient : JsonMcpClient
    {
        private readonly string _settingsDir;

        public ClaudeDesktopClient(string id, string displayName, string settingsDir, Func<bool> installed)
            : base(id, displayName, Path.Combine(settingsDir, "claude_desktop_config.json"), installed, "mcpServers",
                   typed: false, restartNote: "Quit Claude Desktop (from the tray) and start it again to pick it up.")
        {
            _settingsDir = settingsDir;
        }

        public override McpClientState State(McpServerEntry entry)
        {
            McpClientState state = base.State(entry);
            if (state == McpClientState.NotRegistered && HasExtension()) return McpClientState.HasExtension;
            return state;
        }

        public override McpChange Register(McpServerEntry entry)
        {
            if (HasExtension() && base.State(entry) == McpClientState.NotRegistered)
                return new McpChange(false, null, "the GPIB MCP extension is installed in Claude Desktop, which already runs this server");
            return base.Register(entry);
        }

        /// <summary>Whether an installed extension's manifest names this server (Build-Mcpb.ps1 writes "gpib-mcp").</summary>
        private bool HasExtension()
        {
            string extensions = Path.Combine(_settingsDir, "Claude Extensions");
            if (!Directory.Exists(extensions)) return false;
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(extensions))
                {
                    string manifest = Path.Combine(dir, "manifest.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        if ((string)JObject.Parse(File.ReadAllText(manifest))["name"] == "gpib-mcp") return true;
                    }
                    catch (JsonException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return false;
        }
    }

    /// <summary>
    /// Claude Code. Its user settings are <c>~/.claude.json</c>, which it rewrites while it runs, so its own
    /// <c>claude mcp</c> command is used when it is on PATH and the file is edited only when it is not.
    /// </summary>
    public sealed class ClaudeCodeClient : JsonMcpClient
    {
        private readonly McpEnvironment _environment;

        public ClaudeCodeClient(McpEnvironment environment)
            : base("claude-code", "Claude Code", Path.Combine(environment.UserProfile, ".claude.json"),
                   () => environment.OnPath("claude") || File.Exists(Path.Combine(environment.UserProfile, ".claude.json")),
                   "mcpServers", typed: false, restartNote: "Claude Code sessions pick it up when they next start.")
        {
            _environment = environment;
        }

        private bool UseCommand => _environment.OnPath("claude");

        public override string Location => UseCommand ? "claude mcp (user scope)" : ConfigPath;

        public override McpChange Register(McpServerEntry entry)
        {
            if (!UseCommand) return base.Register(entry);
            if (State(entry) == McpClientState.Registered) return new McpChange(false, null);

            _environment.Run("claude", new[] { "mcp", "remove", McpServerEntry.Name, "--scope", "user" });
            var arguments = new List<string> { "mcp", "add", McpServerEntry.Name, "--scope", "user" };
            if (entry.Manuals != null) arguments.AddRange(new[] { "--env", McpServerEntry.ManualsVariable + "=" + entry.Manuals });
            arguments.AddRange(new[] { "--", entry.Command });

            ProcessResult result = _environment.Run("claude", arguments);
            return result.ExitCode == 0 ? new McpChange(true, null)
                                        : new McpChange(false, null, "claude mcp add failed: " + result.Output.Trim());
        }

        public override McpChange Unregister()
        {
            if (!UseCommand) return base.Unregister();
            if (Snapshot() == null) return new McpChange(false, null);

            ProcessResult result = _environment.Run("claude", new[] { "mcp", "remove", McpServerEntry.Name, "--scope", "user" });
            return result.ExitCode == 0 ? new McpChange(true, null)
                                        : new McpChange(false, null, "claude mcp remove failed: " + result.Output.Trim());
        }

        public override McpChange Restore(string snapshot)
        {
            if (!UseCommand) return base.Restore(snapshot);

            _environment.Run("claude", new[] { "mcp", "remove", McpServerEntry.Name, "--scope", "user" });
            ProcessResult result = _environment.Run("claude", new[] { "mcp", "add-json", McpServerEntry.Name, snapshot, "--scope", "user" });
            return result.ExitCode == 0 ? new McpChange(true, null)
                                        : new McpChange(false, null, "claude mcp add-json failed: " + result.Output.Trim());
        }
    }

    /// <summary>
    /// OpenAI's Codex CLI, whose settings are TOML (<c>~/.codex/config.toml</c>). This server owns one table,
    /// <c>[mcp_servers.gpib]</c>, and its <c>env</c> sub-table; it replaces or removes exactly those lines and
    /// leaves the rest of the file as it was.
    /// </summary>
    public sealed class CodexClient : McpClient
    {
        private readonly McpEnvironment _environment;

        public CodexClient(McpEnvironment environment) : base("codex", "OpenAI Codex CLI")
        {
            _environment = environment;
        }

        private string Folder => Path.Combine(_environment.UserProfile, ".codex");
        private string ConfigPath => Path.Combine(Folder, "config.toml");

        public override string Location => ConfigPath;

        public override bool IsInstalled => Directory.Exists(Folder);

        private string[] Lines() => File.Exists(ConfigPath) ? File.ReadAllLines(ConfigPath) : new string[0];

        public override McpClientState State(McpServerEntry entry)
        {
            if (!IsInstalled) return McpClientState.NotInstalled;

            string[] lines = Lines();
            List<int> ours = Ours(lines);
            if (ours.Count == 0) return McpClientState.NotRegistered;
            return ours.Select(i => lines[i]).SequenceEqual(Table(entry)) ? McpClientState.Registered : McpClientState.Different;
        }

        public override McpChange Register(McpServerEntry entry)
        {
            if (State(entry) == McpClientState.Registered) return new McpChange(false, null);
            return new McpChange(true, WriteTables(Table(entry)));
        }

        public override string Snapshot()
        {
            string[] lines = Lines();
            List<int> ours = Ours(lines);
            return ours.Count == 0 ? null : string.Join("\n", ours.Select(i => lines[i]));
        }

        public override string CommandOf(string snapshot)
        {
            string line = snapshot.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("command", StringComparison.Ordinal));
            if (line == null) return null;
            string value = line.Substring(line.IndexOf('=') + 1).Trim();
            if (value.Length >= 2 && value[0] == '\'') return value.Substring(1, value.Length - 2);
            try { return JsonConvert.DeserializeObject<string>(value); }
            catch (JsonException) { return null; }
        }

        public override McpChange Restore(string snapshot)
        {
            WriteTables(snapshot.Split('\n'));
            if (!PutBackIfUnchanged(File.ReadAllLines(ConfigPath))) DropBackup(ConfigPath);
            return new McpChange(true, null);
        }

        /// <summary>Replaces this server's tables with these lines, at the end of the file, and returns the backup.</summary>
        private string WriteTables(IEnumerable<string> tables)
        {
            List<string> lines = Lines().ToList();
            foreach (int index in Ours(lines.ToArray()).OrderByDescending(i => i)) lines.RemoveAt(index);

            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);
            if (lines.Count > 0) lines.Add("");
            lines.AddRange(tables);

            string backup = Backup(ConfigPath);
            Directory.CreateDirectory(Folder);
            File.WriteAllLines(ConfigPath, lines);
            return backup;
        }

        public override McpChange Unregister()
        {
            if (!File.Exists(ConfigPath)) return new McpChange(false, null);

            List<string> lines = File.ReadAllLines(ConfigPath).ToList();
            List<int> ours = Ours(lines.ToArray());
            if (ours.Count == 0) return new McpChange(false, null);

            foreach (int index in ours.OrderByDescending(i => i)) lines.RemoveAt(index);
            while (lines.Count > 0 && string.IsNullOrWhiteSpace(lines[lines.Count - 1])) lines.RemoveAt(lines.Count - 1);

            if (!PutBackIfUnchanged(lines))
            {
                if (lines.Count == 0) File.Delete(ConfigPath);
                else File.WriteAllLines(ConfigPath, lines);
                DropBackup(ConfigPath);
            }
            return new McpChange(true, null);
        }

        /// <summary>As for JSON settings: the copy from before goes back byte for byte when the lines match it.</summary>
        private bool PutBackIfUnchanged(IReadOnlyList<string> lines)
        {
            string backup = BackupPathFor(ConfigPath);
            if (!File.Exists(backup)) return false;

            List<string> before = File.ReadAllLines(backup).ToList();
            while (before.Count > 0 && string.IsNullOrWhiteSpace(before[before.Count - 1])) before.RemoveAt(before.Count - 1);
            if (!before.SequenceEqual(lines)) return false;

            File.Copy(backup, ConfigPath, overwrite: true);
            File.Delete(backup);
            return true;
        }

        internal static IReadOnlyList<string> Table(McpServerEntry entry)
        {
            var table = new List<string>
            {
                "[mcp_servers." + McpServerEntry.Name + "]",
                "command = " + Quote(entry.Command)
            };
            if (entry.Manuals != null)
            {
                table.Add("");
                table.Add("[mcp_servers." + McpServerEntry.Name + ".env]");
                table.Add(McpServerEntry.ManualsVariable + " = " + Quote(entry.Manuals));
            }
            return table;
        }

        /// <summary>The line numbers of this server's tables: each header to the line before the next header.</summary>
        internal static List<int> Ours(string[] lines)
        {
            var ours = new List<int>();
            bool inOurs = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[", StringComparison.Ordinal))
                    inOurs = trimmed == "[mcp_servers." + McpServerEntry.Name + "]" ||
                             trimmed == "[mcp_servers." + McpServerEntry.Name + ".env]";
                if (inOurs) ours.Add(i);
            }
            return ours;
        }

        /// <summary>A TOML literal string: single quotes, no escapes, so Windows paths need nothing doubled.</summary>
        private static string Quote(string value) => value.Contains("'") ? JsonConvert.ToString(value) : "'" + value + "'";
    }

    /// <summary>
    /// Continue, which reads one YAML file per MCP server from <c>~/.continue/mcpServers</c>. This server's is
    /// its own file, so adding and removing it touch nothing else.
    /// </summary>
    public sealed class ContinueClient : McpClient
    {
        private readonly McpEnvironment _environment;

        public ContinueClient(McpEnvironment environment) : base("continue", "Continue")
        {
            _environment = environment;
        }

        private string ConfigPath => Path.Combine(_environment.UserProfile, ".continue", "mcpServers", "gpib-mcp.yaml");

        public override string Location => ConfigPath;

        public override bool IsInstalled => Directory.Exists(Path.Combine(_environment.UserProfile, ".continue"));

        public override McpClientState State(McpServerEntry entry) =>
            !IsInstalled ? McpClientState.NotInstalled
            : !File.Exists(ConfigPath) ? McpClientState.NotRegistered
            : File.ReadAllText(ConfigPath) == Yaml(entry) ? McpClientState.Registered
            : McpClientState.Different;

        public override McpChange Register(McpServerEntry entry)
        {
            if (State(entry) == McpClientState.Registered) return new McpChange(false, null);

            string backup = Backup(ConfigPath);
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, Yaml(entry));
            return new McpChange(true, backup);
        }

        public override McpChange Unregister()
        {
            if (!File.Exists(ConfigPath)) return new McpChange(false, null);
            File.Delete(ConfigPath);
            return new McpChange(true, null);
        }

        public override string Snapshot() => File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : null;

        public override string CommandOf(string snapshot)
        {
            string line = snapshot.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith("command:", StringComparison.Ordinal));
            if (line == null) return null;
            try { return JsonConvert.DeserializeObject<string>(line.Substring(line.IndexOf(':') + 1).Trim()); }
            catch (JsonException) { return null; }
        }

        public override McpChange Restore(string snapshot)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath));
            File.WriteAllText(ConfigPath, snapshot);
            DropBackup(ConfigPath);
            return new McpChange(true, null);
        }

        internal static string Yaml(McpServerEntry entry)
        {
            var lines = new List<string>
            {
                "name: GPIB MCP",
                "version: 0.0.1",
                "schema: v1",
                "mcpServers:",
                "  - name: " + McpServerEntry.Name,
                "    command: " + JsonConvert.ToString(entry.Command)
            };
            if (entry.Manuals != null)
            {
                lines.Add("    env:");
                lines.Add("      " + McpServerEntry.ManualsVariable + ": " + JsonConvert.ToString(entry.Manuals));
            }
            lines.Add("");
            return string.Join("\n", lines);
        }
    }

    /// <summary>Every client this server knows how to register with, as found on this machine.</summary>
    public static class McpClients
    {
        public static IReadOnlyList<McpClient> All(McpEnvironment environment)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            string home = environment.UserProfile;

            // The Microsoft Store build keeps its settings in its package's virtualised AppData, not %APPDATA%.
            string packages = Path.Combine(environment.LocalAppData, "Packages");
            string store = Directory.Exists(packages) ? Directory.GetDirectories(packages, "Claude_*").FirstOrDefault() : null;

            return new McpClient[]
            {
                new ClaudeDesktopClient("claude-desktop-store", "Claude Desktop (Microsoft Store)",
                    Path.Combine(store ?? Path.Combine(packages, "Claude_"), "LocalCache", "Roaming", "Claude"),
                    () => store != null),
                new ClaudeDesktopClient("claude-desktop", "Claude Desktop",
                    Path.Combine(environment.AppData, "Claude"),
                    () => Directory.Exists(Path.Combine(environment.AppData, "Claude"))),
                new ClaudeCodeClient(environment),
                new JsonMcpClient("vscode", "VS Code",
                    Path.Combine(environment.AppData, "Code", "User", "mcp.json"),
                    () => Directory.Exists(Path.Combine(environment.AppData, "Code", "User")), "servers", typed: true),
                new JsonMcpClient("vscode-insiders", "VS Code Insiders",
                    Path.Combine(environment.AppData, "Code - Insiders", "User", "mcp.json"),
                    () => Directory.Exists(Path.Combine(environment.AppData, "Code - Insiders", "User")), "servers", typed: true),
                new JsonMcpClient("visual-studio", "Visual Studio",
                    Path.Combine(home, ".mcp.json"),
                    () => VisualStudioInstalled(environment), "servers", typed: true,
                    restartNote: "Visual Studio picks it up when it next starts."),
                new JsonMcpClient("cursor", "Cursor",
                    Path.Combine(home, ".cursor", "mcp.json"),
                    () => Directory.Exists(Path.Combine(home, ".cursor")), "mcpServers", typed: false),
                new JsonMcpClient("windsurf", "Windsurf",
                    Path.Combine(home, ".codeium", "windsurf", "mcp_config.json"),
                    () => Directory.Exists(Path.Combine(home, ".codeium", "windsurf")), "mcpServers", typed: false),
                new JsonMcpClient("gemini", "Gemini CLI",
                    Path.Combine(home, ".gemini", "settings.json"),
                    () => Directory.Exists(Path.Combine(home, ".gemini")), "mcpServers", typed: false),
                new CodexClient(environment),
                new JsonMcpClient("lmstudio", "LM Studio",
                    Path.Combine(home, ".lmstudio", "mcp.json"),
                    () => Directory.Exists(Path.Combine(home, ".lmstudio")), "mcpServers", typed: false),
                new ContinueClient(environment)
            };
        }

        private static bool VisualStudioInstalled(McpEnvironment environment)
        {
            string root = Path.Combine(environment.ProgramFiles, "Microsoft Visual Studio");
            try
            {
                return Directory.Exists(root) &&
                       Directory.EnumerateDirectories(root).SelectMany(Directory.EnumerateDirectories)
                                .Any(edition => File.Exists(Path.Combine(edition, "Common7", "IDE", "devenv.exe")));
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
