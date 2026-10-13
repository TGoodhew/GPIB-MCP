using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Mcp;

namespace GpibMcp.Setup
{
    /// <summary>
    /// What <c>GpibMcp.exe</c> does when given a command rather than started by an MCP client: report its
    /// version, check the NI drivers, and register itself with the AI apps on this PC. The installer drives all
    /// three, and leans on the program to do them because the program is still there to undo them when it is
    /// uninstalled - the pattern ManualForge's installer settled on.
    ///
    /// With no arguments the server runs as it always has; nothing here is reached.
    /// </summary>
    public static class SetupCommands
    {
        public const int ExitOk = 0;
        public const int ExitFailed = 1;
        public const int ExitUsage = 2;

        /// <summary>The NI check found something missing - distinct, so the installer can tell it from a crash.</summary>
        public const int ExitNiMissing = 3;

        /// <summary>
        /// Runs the command in <paramref name="args"/>, or returns null when there is none and the caller should
        /// serve MCP as usual.
        /// </summary>
        public static int? Run(string[] args, TextWriter output, TextWriter error, string exeDir,
                               McpEnvironment environment = null)
        {
            if (args == null || args.Length == 0) return null;

            switch (args[0].ToLowerInvariant())
            {
                case "--version":
                case "version":
                    output.WriteLine(McpDispatcher.ServerName + " " + NiCheck.ProgramVersion());
                    return ExitOk;

                case "check":
                case "--check":
                    return Check(args, output, exeDir);

                case "setup":
                    if (args.Length >= 2 && args[1].Equals("mcp", StringComparison.OrdinalIgnoreCase))
                        return SetupMcp(Options(args.Skip(2)), output, error, exeDir,
                                        environment ?? McpEnvironment.FromProcess());
                    break;

                case "help":
                case "--help":
                case "-h":
                case "/?":
                    Usage(output);
                    return ExitOk;
            }

            error.WriteLine("error: unknown command '" + string.Join(" ", args) + "'.");
            Usage(error);
            return ExitUsage;
        }

        private static void Usage(TextWriter to)
        {
            to.WriteLine("usage:");
            to.WriteLine("  GpibMcp.exe                      serve MCP over stdio (what MCP clients run)");
            to.WriteLine("  GpibMcp.exe --version            which build this is");
            to.WriteLine("  GpibMcp.exe check [--no-start]   whether the NI drivers GPIB MCP needs are installed");
            to.WriteLine("  GpibMcp.exe setup mcp [--manuals <folder> | --no-manuals] [--client <id,...>] [--list]");
            to.WriteLine("                                   register with the AI apps on this PC");
            to.WriteLine("  GpibMcp.exe setup mcp --remove [--all] [--client <id,...>]");
            to.WriteLine("                                   undo that (--all: every entry named '" + McpServerEntry.Name + "')");
        }

        // ---- check -------------------------------------------------------------------------------------

        private static int Check(string[] args, TextWriter output, string exeDir)
        {
            bool start = !args.Skip(1).Any(a => a.Equals("--no-start", StringComparison.OrdinalIgnoreCase));
            NiCheck.Result result = new NiCheck(exeDir).Run(start);

            output.WriteLine(McpDispatcher.ServerName + " " + NiCheck.ProgramVersion() + ": NI drivers");
            output.WriteLine();
            output.Write(result.Report());
            return result.Ok ? ExitOk : ExitNiMissing;
        }

        // ---- setup mcp ---------------------------------------------------------------------------------

        private static Dictionary<string, string> Options(IEnumerable<string> args)
        {
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string pending = null;
            foreach (string arg in args)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    if (pending != null) options[pending] = "";
                    pending = arg.Substring(2);
                }
                else if (pending != null)
                {
                    options[pending] = arg;
                    pending = null;
                }
            }
            if (pending != null) options[pending] = "";
            return options;
        }

        private static int SetupMcp(Dictionary<string, string> options, TextWriter output, TextWriter error,
                                    string exeDir, McpEnvironment environment)
        {
            string only;
            string[] wanted = options.TryGetValue("client", out only) && only.Length > 0
                ? only.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToArray()
                : null;
            List<McpClient> clients = McpClients.All(environment)
                .Where(c => wanted == null || wanted.Contains(c.Id, StringComparer.OrdinalIgnoreCase))
                .ToList();

            McpRegistrations record = McpRegistrations.Load(environment);

            if (options.ContainsKey("remove")) return Remove(options, clients, record, output);

            string server;
            if (!options.TryGetValue("server", out server) || server.Length == 0)
                server = Path.Combine(exeDir, "GpibMcp.exe");

            string manuals;
            if (options.ContainsKey("no-manuals")) manuals = null;
            else if (!options.TryGetValue("manuals", out manuals) || manuals.Length == 0)
                manuals = Environment.GetEnvironmentVariable(McpServerEntry.ManualsVariable);

            bool listing = options.ContainsKey("list");
            if (!listing && !File.Exists(server))
            {
                error.WriteLine("error: there is no server at " + server + ". Run this from the installed GPIB MCP, or pass --server.");
                return ExitUsage;
            }

            var entry = new McpServerEntry(Path.GetFullPath(server),
                                           string.IsNullOrWhiteSpace(manuals) ? null : Path.GetFullPath(manuals.Trim().Trim('"')));
            output.WriteLine("MCP server : " + entry.Command);
            output.WriteLine("Manuals    : " + (entry.Manuals ?? "(none - manual lookup off)"));
            output.WriteLine();

            var restart = new List<string>();
            foreach (McpClient client in clients)
            {
                McpClientState state = client.State(entry);
                string outcome;
                if (listing || state == McpClientState.NotInstalled || state == McpClientState.Unreadable ||
                    state == McpClientState.HasExtension)
                {
                    outcome = Describe(state, listing);
                }
                else if (state == McpClientState.Registered)
                {
                    outcome = "already registered";
                }
                else
                {
                    McpChange change = record.Register(client, entry);
                    outcome = !change.Changed ? "not changed: " + change.Note
                        : (state == McpClientState.Different ? "updated" : "registered")
                          + (change.Backup == null ? "" : "; previous settings copied aside")
                          + (change.Note == null ? "" : "; " + change.Note);
                    if (change.Changed && client.RestartNote != null) restart.Add(client.RestartNote);
                }

                output.WriteLine("  " + client.DisplayName.PadRight(34) + " " + outcome);
                if (state != McpClientState.NotInstalled)
                    output.WriteLine("  " + "".PadRight(34) + " " + client.Location);
            }

            foreach (string note in restart.Distinct())
            {
                output.WriteLine();
                output.WriteLine(note);
            }
            return ExitOk;
        }

        private static string Describe(McpClientState state, bool listing)
        {
            switch (state)
            {
                case McpClientState.NotInstalled: return "not installed";
                case McpClientState.Unreadable: return "settings unreadable; left alone";
                case McpClientState.HasExtension: return "runs GPIB MCP as an extension; left alone";
                case McpClientState.Registered: return "registered";
                case McpClientState.Different: return listing ? "registered, pointing elsewhere" : "would be updated";
                default: return listing ? "not registered" : "would be registered";
            }
        }

        private static int Remove(Dictionary<string, string> options, List<McpClient> clients,
                                  McpRegistrations record, TextWriter output)
        {
            Func<McpChange, string> kept = change =>
                (change.Backup == null ? "" : "   (the copy from before GPIB MCP is kept, for its comments: " + change.Backup + ")") +
                (change.Note == null ? "" : "   " + change.Note);

            // --all is for cleaning up by hand: every entry named gpib, whoever put it there.
            if (options.ContainsKey("all"))
            {
                foreach (McpClient client in clients.Where(c => c.IsInstalled))
                {
                    McpChange change = client.Unregister();
                    output.WriteLine("  " + client.DisplayName.PadRight(34) + " " + (change.Changed ? "removed" : "had none") + kept(change));
                }
                record.Remove(clients);
                return ExitOk;
            }

            IReadOnlyList<McpRemoval> removals = record.Remove(clients);
            if (removals.Count == 0)
                output.WriteLine("  GPIB MCP has no record of registering with any app here; nothing removed. " +
                                 "--all removes every entry named '" + McpServerEntry.Name + "'.");

            foreach (McpRemoval removal in removals)
            {
                string outcome = !removal.Change.Changed && removal.Change.Note != null ? "not changed"
                    : removal.Restored ? "put back the entry it had before"
                    : removal.Change.Changed ? "removed" : "had none";
                output.WriteLine("  " + removal.Client.DisplayName.PadRight(34) + " " + outcome + kept(removal.Change));
            }

            return removals.Any(r => !r.Change.Changed && r.Change.Note != null) ? ExitFailed : ExitOk;
        }
    }
}
