using System;
using System.Collections.Generic;
using System.IO;
using GpibMcp.Diagnostics;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// Resolves the on-disk locations for the instrument database and assignment store, and
    /// performs first-run prepopulation of the user database from the bundled defaults.
    ///
    /// Layout:
    ///   - Bundled defaults: &lt;exeDir&gt;\data\instruments\*.json  (ships with the build)
    ///   - User database:    %LOCALAPPDATA%\GpibMcp\instruments     (override via GPIB_MCP_INSTRUMENT_DB)
    ///   - Assignments:      %LOCALAPPDATA%\GpibMcp\bindings.json    (override via GPIB_MCP_BINDINGS)
    /// </summary>
    public static class InstrumentPaths
    {
        public static string BundledDatabaseDir(string exeDir) =>
            string.IsNullOrEmpty(exeDir) ? null : Path.Combine(exeDir, "data", "instruments");

        public static string UserDatabaseDir()
        {
            string env = Environment.GetEnvironmentVariable("GPIB_MCP_INSTRUMENT_DB");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Path.Combine(AppDataDir(), "instruments");
        }

        /// <summary>Database load order: bundled defaults first, then the user dir (user wins).</summary>
        public static IEnumerable<string> DatabaseDirectories(string exeDir)
        {
            yield return BundledDatabaseDir(exeDir);
            yield return UserDatabaseDir();
        }

        public static string BindingsPath()
        {
            string env = Environment.GetEnvironmentVariable("GPIB_MCP_BINDINGS");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Path.Combine(AppDataDir(), "bindings.json");
        }

        /// <summary>
        /// On first run, copies the bundled definitions into the user database directory so the
        /// user has an editable, prepopulated database. Never overwrites existing user files.
        /// </summary>
        public static void EnsureUserDatabaseSeeded(string exeDir)
        {
            try
            {
                string bundled = BundledDatabaseDir(exeDir);
                string userDir = UserDatabaseDir();
                if (string.IsNullOrEmpty(bundled) || !Directory.Exists(bundled)) return;

                Directory.CreateDirectory(userDir);
                int copied = 0;
                foreach (var src in Directory.GetFiles(bundled, "*.json"))
                {
                    string dest = Path.Combine(userDir, Path.GetFileName(src));
                    if (File.Exists(dest)) continue; // never clobber user edits
                    File.Copy(src, dest);
                    copied++;
                }
                if (copied > 0)
                    Log.Info("Prepopulated user instrument database with " + copied + " definition(s) at " + userDir);
            }
            catch (Exception ex)
            {
                Log.Warn("Could not prepopulate user instrument database: " + ex.Message);
            }
        }

        private static string AppDataDir() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GpibMcp");
    }
}
