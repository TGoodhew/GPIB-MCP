using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace GpibMcp.Setup
{
    /// <summary>
    /// Where a user's applications keep their settings, read from the environment rather than from the
    /// shell's known folders, so a test can point every client somewhere harmless. Ported from ManualForge's
    /// installer work (TGoodhew/ManualForge#26), which found each of these the hard way.
    /// </summary>
    public sealed class McpEnvironment
    {
        public McpEnvironment(string userProfile, string appData, string localAppData, string programFiles,
                              Func<string, bool> onPath, Func<string, IReadOnlyList<string>, ProcessResult> run,
                              Func<string, string> variable = null,
                              Func<IReadOnlyList<RunningProcess>> runningClaude = null)
        {
            UserProfile = userProfile;
            AppData = appData;
            LocalAppData = localAppData;
            ProgramFiles = programFiles;
            OnPath = onPath;
            Run = run;
            Variable = variable ?? (_ => null);
            RunningClaude = runningClaude ?? (() => new RunningProcess[0]);
        }

        public string UserProfile { get; }
        public string AppData { get; }
        public string LocalAppData { get; }
        public string ProgramFiles { get; }

        /// <summary>Whether a command (with .exe, .cmd or .bat) is on PATH.</summary>
        public Func<string, bool> OnPath { get; }

        /// <summary>Runs a command with arguments and returns its exit code and output.</summary>
        public Func<string, IReadOnlyList<string>, ProcessResult> Run { get; }

        /// <summary>An environment variable, or null when it isn't set.</summary>
        public Func<string, string> Variable { get; }

        /// <summary>
        /// The claude.exe processes running now, with their command lines, so Claude Desktop's own
        /// --user-data-dir can say where its settings are (#175). Empty when none, or when they can't be read.
        /// </summary>
        public Func<IReadOnlyList<RunningProcess>> RunningClaude { get; }

        public static McpEnvironment FromProcess() => new McpEnvironment(
            Folder("USERPROFILE", Environment.SpecialFolder.UserProfile),
            Folder("APPDATA", Environment.SpecialFolder.ApplicationData),
            Folder("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
            Folder("ProgramFiles", Environment.SpecialFolder.ProgramFiles),
            IsOnPath,
            RunProcess,
            name => { string value = Environment.GetEnvironmentVariable(name); return string.IsNullOrEmpty(value) ? null : value; },
            FindRunningClaude);

        private static string Folder(string name, Environment.SpecialFolder fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? Environment.GetFolderPath(fallback) : value;
        }

        private static IReadOnlyList<RunningProcess> FindRunningClaude()
        {
            var found = new List<RunningProcess>();
            try
            {
                using (var searcher = new System.Management.ManagementObjectSearcher(
                           "SELECT ExecutablePath, CommandLine FROM Win32_Process WHERE Name = 'claude.exe'"))
                using (var results = searcher.Get())
                {
                    foreach (var process in results)
                    {
                        using (process)
                        {
                            var path = process["ExecutablePath"] as string;
                            var commandLine = process["CommandLine"] as string;
                            if (path != null && commandLine != null) found.Add(new RunningProcess(path, commandLine));
                        }
                    }
                }
            }
            catch (Exception e) when (e is System.Management.ManagementException || e is System.Runtime.InteropServices.COMException ||
                                      e is UnauthorizedAccessException)
            {
                // No WMI, or not allowed to ask: fall back to what's on disk.
            }
            return found;
        }

        private static bool IsOnPath(string command) => FindOnPath(command) != null;

        private static string FindOnPath(string command)
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (string extension in new[] { ".exe", ".cmd", ".bat" })
                {
                    string candidate;
                    try { candidate = Path.Combine(dir.Trim().Trim('"'), command + extension); }
                    catch (ArgumentException) { continue; }   // a malformed PATH entry
                    if (File.Exists(candidate)) return candidate;
                }
            }
            return null;
        }

        private static ProcessResult RunProcess(string command, IReadOnlyList<string> arguments)
        {
            // .NET Framework has no ArgumentList, so each argument is quoted the way the C runtime splits
            // them; and a .cmd shim (npm installs claude as one) has to be found by its full name.
            string resolved = FindOnPath(command) ?? command;
            var start = new ProcessStartInfo(resolved, string.Join(" ", arguments.Select(Manuals.ManualForgeIndex.QuoteArgument)))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (var process = Process.Start(start))
            {
                var stderr = process.StandardError.ReadToEndAsync();
                string output = process.StandardOutput.ReadToEnd();
                if (!process.WaitForExit(60000))
                {
                    try { process.Kill(); } catch { /* already gone */ }
                    return new ProcessResult(-1, output + "\n(timed out after 60 s)");
                }
                return new ProcessResult(process.ExitCode, output + stderr.Result);
            }
        }
    }

    /// <summary>A process that is running: its program and its whole command line.</summary>
    public sealed class RunningProcess
    {
        public RunningProcess(string executablePath, string commandLine)
        {
            ExecutablePath = executablePath ?? "";
            CommandLine = commandLine ?? "";
        }

        public string ExecutablePath { get; }
        public string CommandLine { get; }
    }

    public sealed class ProcessResult
    {
        public ProcessResult(int exitCode, string output)
        {
            ExitCode = exitCode;
            Output = output ?? "";
        }

        public int ExitCode { get; }
        public string Output { get; }
    }
}
