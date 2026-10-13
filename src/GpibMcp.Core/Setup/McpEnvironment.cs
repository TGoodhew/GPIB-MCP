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
                              Func<string, bool> onPath, Func<string, IReadOnlyList<string>, ProcessResult> run)
        {
            UserProfile = userProfile;
            AppData = appData;
            LocalAppData = localAppData;
            ProgramFiles = programFiles;
            OnPath = onPath;
            Run = run;
        }

        public string UserProfile { get; }
        public string AppData { get; }
        public string LocalAppData { get; }
        public string ProgramFiles { get; }

        /// <summary>Whether a command (with .exe, .cmd or .bat) is on PATH.</summary>
        public Func<string, bool> OnPath { get; }

        /// <summary>Runs a command with arguments and returns its exit code and output.</summary>
        public Func<string, IReadOnlyList<string>, ProcessResult> Run { get; }

        public static McpEnvironment FromProcess() => new McpEnvironment(
            Variable("USERPROFILE", Environment.SpecialFolder.UserProfile),
            Variable("APPDATA", Environment.SpecialFolder.ApplicationData),
            Variable("LOCALAPPDATA", Environment.SpecialFolder.LocalApplicationData),
            Variable("ProgramFiles", Environment.SpecialFolder.ProgramFiles),
            IsOnPath,
            RunProcess);

        private static string Variable(string name, Environment.SpecialFolder fallback)
        {
            string value = Environment.GetEnvironmentVariable(name);
            return string.IsNullOrEmpty(value) ? Environment.GetFolderPath(fallback) : value;
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
