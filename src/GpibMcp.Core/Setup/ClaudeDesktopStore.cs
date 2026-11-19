using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace GpibMcp.Setup
{
    /// <summary>
    /// Where the Microsoft Store (MSIX) build of Claude Desktop keeps its settings (#175). Anthropic doesn't
    /// document it, and it has moved. Older builds use the package's virtualised AppData,
    /// <c>%LOCALAPPDATA%\Packages\&lt;family&gt;\LocalCache\Roaming\Claude</c>. Current builds move their data
    /// to <c>%LOCALAPPDATA%\Claude-Data\&lt;family, lower case&gt;</c> when they update, run with
    /// <c>--user-data-dir</c> pointing there, and leave a <c>.user-data-left.&lt;family&gt;</c> marker in the
    /// old folder, whose config they no longer read. The details are from anthropics/claude-code#26073 and
    /// from Claude Desktop 6.6395 on a real PC.
    /// </summary>
    public static class ClaudeDesktopStore
    {
        /// <summary>The package names Claude Desktop is published under; a family name is "&lt;name&gt;_&lt;publisher id&gt;".</summary>
        private static readonly string[] PackageNames = { "Claude", "AnthropicPBC.Claude" };

        /// <summary>Overrides the data folder, when set. Claude Desktop reads it itself.</summary>
        public const string UserDataVariable = "CLAUDE_USER_DATA_DIR";

        private const string MovedMarker = ".user-data-left.";

        private static readonly Regex UserDataDir = new Regex(@"--user-data-dir=(?:""([^""]+)""|(\S+))", RegexOptions.IgnoreCase);

        /// <summary>
        /// Finds the Store build's settings folder. False, with a null folder, when the Store build isn't
        /// installed. In order:
        /// 1. a running Store Claude Desktop's own --user-data-dir: the folder it's using right now;
        /// 2. CLAUDE_USER_DATA_DIR;
        /// 3. for the installed package, Claude-Data\&lt;family&gt; when it exists or the old folder's marker
        ///    says the data moved, else the old LocalCache folder: an older build that hasn't moved yet, and
        ///    the move takes the config with it.
        /// </summary>
        public static bool TryFind(McpEnvironment environment, out string settingsDir)
        {
            if (environment == null) throw new ArgumentNullException(nameof(environment));
            settingsDir = null;

            IReadOnlyList<string> installed = InstalledFamilies(environment);
            string running = RunningDataDir(environment);
            if (installed.Count == 0 && running == null) return false;

            settingsDir = running ?? Overridden(environment) ?? SettingsFor(environment, installed[0]);
            return true;
        }

        /// <summary>The installed Store packages' family names, from their folders under %LOCALAPPDATA%\Packages.</summary>
        internal static IReadOnlyList<string> InstalledFamilies(McpEnvironment environment)
        {
            string packages = Path.Combine(environment.LocalAppData, "Packages");
            if (!Directory.Exists(packages)) return new string[0];
            return PackageNames
                .SelectMany(name => Directory.GetDirectories(packages, name + "_*"))
                .Select(Path.GetFileName)
                .Where(family => IsFamily(family))
                .ToList();
        }

        /// <summary>"Claude_pzs8sxrjxfjjc", not "Claude_Something_Else": a package name, then one publisher id.</summary>
        private static bool IsFamily(string folder)
        {
            int split = folder.LastIndexOf('_');
            return split > 0 && PackageNames.Contains(folder.Substring(0, split), StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The data folder of a Store Claude Desktop that is running now, or null.</summary>
        internal static string RunningDataDir(McpEnvironment environment)
        {
            IReadOnlyList<RunningProcess> processes;
            try { processes = environment.RunningClaude(); }
            catch (Exception) { return null; }   // only a better answer than the files; never a reason to fail

            foreach (RunningProcess process in processes ?? new RunningProcess[0])
            {
                if (FamilyOf(process.ExecutablePath) == null) continue;
                Match match = UserDataDir.Match(process.CommandLine);
                if (!match.Success) continue;
                string dir = match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
                if (dir.Length > 0) return dir.TrimEnd('\\');
            }
            return null;
        }

        /// <summary>
        /// The family name of the Store package a program runs from, or null when it isn't one. The program is at
        /// ...\WindowsApps\&lt;name&gt;_&lt;version&gt;_&lt;arch&gt;_&lt;resource&gt;_&lt;publisher id&gt;\..., and the
        /// family is &lt;name&gt;_&lt;publisher id&gt;.
        /// </summary>
        internal static string FamilyOf(string executablePath)
        {
            if (string.IsNullOrEmpty(executablePath)) return null;
            string[] parts = executablePath.Split('\\', '/');
            int apps = Array.FindIndex(parts, p => p.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase));
            if (apps < 0 || apps + 1 >= parts.Length) return null;

            string[] full = parts[apps + 1].Split('_');
            if (full.Length < 5) return null;
            string family = full[0] + "_" + full[full.Length - 1];
            return IsFamily(family) ? family : null;
        }

        private static string Overridden(McpEnvironment environment)
        {
            string dir = environment.Variable(UserDataVariable);
            return string.IsNullOrWhiteSpace(dir) ? null : dir.Trim().Trim('"');
        }

        /// <summary>Where a package's settings are when the app isn't running to say.</summary>
        internal static string SettingsFor(McpEnvironment environment, string family)
        {
            string moved = Path.Combine(environment.LocalAppData, "Claude-Data", family.ToLowerInvariant());
            string old = Path.Combine(environment.LocalAppData, "Packages", family, "LocalCache", "Roaming", "Claude");
            bool left = Directory.Exists(old) && Directory.EnumerateFiles(old, MovedMarker + "*").Any();
            return Directory.Exists(moved) || left ? moved : old;
        }
    }
}
