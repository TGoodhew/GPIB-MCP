using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GpibMcp.Setup;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// The installer's helpers: registering with MCP clients and undoing it exactly, and the NI check that
    /// decides whether installing can go ahead. Every client is pointed at a scratch profile, so nothing here
    /// touches the settings of the apps on the machine running the tests.
    /// </summary>
    public class SetupTests : IDisposable
    {
        private readonly string _home;
        private readonly string _appData;
        private readonly string _localAppData;
        private readonly List<string[]> _claudeCalls = new List<string[]>();
        private bool _claudeOnPath;

        public SetupTests()
        {
            _home = Path.Combine(Path.GetTempPath(), "gpibmcp-setup-" + Guid.NewGuid().ToString("N"));
            _appData = Path.Combine(_home, "AppData", "Roaming");
            _localAppData = Path.Combine(_home, "AppData", "Local");
            Directory.CreateDirectory(_appData);
            Directory.CreateDirectory(_localAppData);
        }

        public void Dispose()
        {
            try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
        }

        private McpEnvironment Environment() => new McpEnvironment(
            _home, _appData, _localAppData, Path.Combine(_home, "ProgramFiles"),
            command => command == "claude" && _claudeOnPath,
            (command, arguments) => { _claudeCalls.Add(arguments.ToArray()); return new ProcessResult(0, ""); });

        private McpClient Client(string id) => McpClients.All(Environment()).Single(c => c.Id == id);

        private string VsCodeConfig => Path.Combine(_appData, "Code", "User", "mcp.json");
        private string DesktopDir => Path.Combine(_appData, "Claude");
        private string DesktopConfig => Path.Combine(DesktopDir, "claude_desktop_config.json");

        private static readonly McpServerEntry Entry = new McpServerEntry(@"C:\Programs\GpibMcp\GpibMcp.exe", @"D:\Manuals");

        // ---------------------------------------------------------------- JSON clients

        [Fact]
        public void AnUninstalledClientIsLeftAlone()
        {
            Assert.Equal(McpClientState.NotInstalled, Client("cursor").State(Entry));
            Assert.False(Directory.Exists(Path.Combine(_home, ".cursor")));
        }

        [Fact]
        public void RegisteringWritesTheEntryInTheClientsOwnShape()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VsCodeConfig));
            McpClient vscode = Client("vscode");

            Assert.True(vscode.Register(Entry).Changed);

            JObject gpib = (JObject)JObject.Parse(File.ReadAllText(VsCodeConfig))["servers"]["gpib"];
            Assert.Equal("stdio", (string)gpib["type"]);                       // VS Code's "servers" are typed
            Assert.Equal(Entry.Command, (string)gpib["command"]);
            Assert.Equal(Entry.Manuals, (string)gpib["env"]["GPIB_MCP_MANUALS"]);
            Assert.Equal(McpClientState.Registered, vscode.State(Entry));
            Assert.False(vscode.Register(Entry).Changed);                      // idempotent
        }

        [Fact]
        public void WithNoManualFolderThereIsNoEnvironmentBlock()
        {
            Directory.CreateDirectory(DesktopDir);
            Client("claude-desktop").Register(new McpServerEntry(Entry.Command, null));

            JObject gpib = (JObject)JObject.Parse(File.ReadAllText(DesktopConfig))["mcpServers"]["gpib"];
            Assert.Null(gpib["env"]);
            Assert.Null(gpib["type"]);                                         // mcpServers are untyped
        }

        [Fact]
        public void OtherServersInTheFileAreKept_AndRemovingPutsTheFileBackExactly()
        {
            Directory.CreateDirectory(DesktopDir);
            string original = "{\n  // the user's own comment\n  \"mcpServers\": { \"manualforge\": { \"command\": \"mf.exe\" } }\n}\n";
            File.WriteAllText(DesktopConfig, original);
            McpClient desktop = Client("claude-desktop");

            McpChange change = desktop.Register(Entry);
            Assert.NotNull(change.Backup);                                     // the file as it was, copied aside
            Assert.Contains("comments", change.Note);
            JObject servers = (JObject)JObject.Parse(File.ReadAllText(DesktopConfig))["mcpServers"];
            Assert.NotNull(servers["manualforge"]);
            Assert.NotNull(servers["gpib"]);

            Assert.True(desktop.Unregister().Changed);
            Assert.Equal(original, File.ReadAllText(DesktopConfig));            // byte for byte, comment and all
            Assert.False(File.Exists(DesktopConfig + ".before-gpib-mcp.bak"));
        }

        [Fact]
        public void AFileThisCreatedIsDeletedWhenItsEntryGoes()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VsCodeConfig));
            McpClient vscode = Client("vscode");
            vscode.Register(Entry);
            vscode.Unregister();
            Assert.False(File.Exists(VsCodeConfig));
        }

        [Fact]
        public void UnreadableSettingsAreNeverOverwritten()
        {
            Directory.CreateDirectory(DesktopDir);
            File.WriteAllText(DesktopConfig, "{ this is not json");
            McpClient desktop = Client("claude-desktop");

            Assert.Equal(McpClientState.Unreadable, desktop.State(Entry));
            Assert.False(desktop.Register(Entry).Changed);
            Assert.Equal("{ this is not json", File.ReadAllText(DesktopConfig));
        }

        [Fact]
        public void ClaudeDesktopWithTheExtensionInstalledIsNotGivenASecondCopy()
        {
            // Two servers both claiming the GPIB bus is worse than none: the .mcpb already runs this one.
            string extension = Path.Combine(DesktopDir, "Claude Extensions", "local.mcpb.someone.gpib-mcp");
            Directory.CreateDirectory(extension);
            File.WriteAllText(Path.Combine(extension, "manifest.json"), "{ \"name\": \"gpib-mcp\" }");
            McpClient desktop = Client("claude-desktop");

            Assert.Equal(McpClientState.HasExtension, desktop.State(Entry));
            Assert.False(desktop.Register(Entry).Changed);
            Assert.False(File.Exists(DesktopConfig));
        }

        [Fact]
        public void TheStoreBuildOfClaudeDesktopIsFoundInItsPackageFolder()
        {
            string store = Path.Combine(_localAppData, "Packages", "Claude_abc123", "LocalCache", "Roaming", "Claude");
            Directory.CreateDirectory(store);
            McpClient client = Client("claude-desktop-store");

            Assert.True(client.IsInstalled);
            client.Register(Entry);
            Assert.True(File.Exists(Path.Combine(store, "claude_desktop_config.json")));
        }

        // ---------------------------------------------------------------- Claude Code

        [Fact]
        public void ClaudeCodeIsRegisteredThroughItsOwnCommandWhenItIsOnPath()
        {
            _claudeOnPath = true;
            Assert.True(Client("claude-code").Register(Entry).Changed);

            string[] add = _claudeCalls.Single(c => c[1] == "add");
            Assert.Equal(new[] { "mcp", "add", "gpib", "--scope", "user", "--env", @"GPIB_MCP_MANUALS=D:\Manuals", "--", Entry.Command }, add);
            Assert.False(File.Exists(Path.Combine(_home, ".claude.json")));   // the file it owns is not edited
        }

        [Fact]
        public void ClaudeCodeIsRegisteredInItsFileWhenTheCommandIsNotOnPath()
        {
            File.WriteAllText(Path.Combine(_home, ".claude.json"), "{ \"numStartups\": 3 }");
            Client("claude-code").Register(Entry);

            JObject root = JObject.Parse(File.ReadAllText(Path.Combine(_home, ".claude.json")));
            Assert.Equal(3, (int)root["numStartups"]);
            Assert.Equal(Entry.Command, (string)root["mcpServers"]["gpib"]["command"]);
            Assert.Empty(_claudeCalls);
        }

        [Fact]
        public void RemovingFromAFileThatHadNoServersLeavesNoEmptyServersBehind()
        {
            // Found by installing and uninstalling for real: Claude Code's file kept "mcpServers": {}.
            string config = Path.Combine(_home, ".claude.json");
            string original = "{ \"numStartups\": 3 }\r\n";
            File.WriteAllText(config, original);
            McpClient code = Client("claude-code");

            code.Register(Entry);
            code.Unregister();

            Assert.Equal(original, File.ReadAllText(config));
        }

        // ---------------------------------------------------------------- the other formats

        [Fact]
        public void CodexGetsItsOwnTomlTables_AndTheRestOfTheFileIsKept()
        {
            string config = Path.Combine(_home, ".codex", "config.toml");
            Directory.CreateDirectory(Path.GetDirectoryName(config));
            File.WriteAllText(config, "model = \"o4\"\n");
            McpClient codex = Client("codex");

            codex.Register(Entry);
            string text = File.ReadAllText(config);
            Assert.Contains("[mcp_servers.gpib]", text);
            Assert.Contains(@"command = 'C:\Programs\GpibMcp\GpibMcp.exe'", text);
            Assert.Equal(Entry.Command, codex.CommandOf(codex.Snapshot()));

            codex.Unregister();
            Assert.Equal("model = \"o4\"\n", File.ReadAllText(config).Replace("\r\n", "\n"));
        }

        [Fact]
        public void ContinueGetsAFileOfItsOwn()
        {
            Directory.CreateDirectory(Path.Combine(_home, ".continue"));
            McpClient client = Client("continue");

            client.Register(Entry);
            Assert.Equal(McpClientState.Registered, client.State(Entry));
            Assert.Equal(Entry.Command, client.CommandOf(client.Snapshot()));

            client.Unregister();
            Assert.False(File.Exists(client.Location));
        }

        // ---------------------------------------------------------------- the record, and uninstalling

        [Fact]
        public void UninstallingPutsBackAnEntryThatWasThereBefore()
        {
            Directory.CreateDirectory(DesktopDir);
            File.WriteAllText(DesktopConfig, "{ \"mcpServers\": { \"gpib\": { \"command\": \"D:\\\\dev\\\\GpibMcp.exe\" } } }");
            McpRegistrations record = McpRegistrations.Load(Environment());

            record.Register(Client("claude-desktop"), Entry);
            Assert.Equal(Entry.Command, (string)JObject.Parse(File.ReadAllText(DesktopConfig))["mcpServers"]["gpib"]["command"]);

            McpRemoval removal = McpRegistrations.Load(Environment()).Remove(McpClients.All(Environment())).Single();
            Assert.True(removal.Restored);
            Assert.Equal(@"D:\dev\GpibMcp.exe", (string)JObject.Parse(File.ReadAllText(DesktopConfig))["mcpServers"]["gpib"]["command"]);
            Assert.False(File.Exists(McpRegistrations.PathFor(Environment())));   // nothing left to undo
        }

        [Fact]
        public void AnEarlierEntryForTheSameProgramIsNotPutBack()
        {
            // The old install script wrote "gpib" -> this same exe. Restoring that on uninstall would leave the
            // client pointing at a program that has just been removed.
            Directory.CreateDirectory(DesktopDir);
            File.WriteAllText(DesktopConfig, "{ \"mcpServers\": { \"gpib\": { \"command\": \"C:\\\\Programs\\\\GpibMcp\\\\GpibMcp.exe\", \"args\": [] } } }");
            McpRegistrations.Load(Environment()).Register(Client("claude-desktop"), Entry);

            McpRemoval removal = McpRegistrations.Load(Environment()).Remove(McpClients.All(Environment())).Single();
            Assert.False(removal.Restored);
            Assert.Null(JObject.Parse(File.ReadAllText(DesktopConfig))["mcpServers"]["gpib"]);
        }

        [Fact]
        public void AnEntryFoundAlreadyRegisteredIsNotRecorded_SoUninstallingLeavesIt()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(VsCodeConfig));
            Client("vscode").Register(Entry);                                  // put there by the user, say
            McpRegistrations.Load(Environment()).Register(Client("vscode"), Entry);

            Assert.Empty(McpRegistrations.Load(Environment()).Remove(McpClients.All(Environment())));
            Assert.Equal(McpClientState.Registered, Client("vscode").State(Entry));
        }

        // ---------------------------------------------------------------- the command line

        [Fact]
        public void NoArgumentsMeansServeMcp()
        {
            Assert.Null(SetupCommands.Run(new string[0], TextWriter.Null, TextWriter.Null, _home));
        }

        [Fact]
        public void AnUnknownCommandIsAUsageError()
        {
            var error = new StringWriter();
            Assert.Equal(SetupCommands.ExitUsage, SetupCommands.Run(new[] { "frobnicate" }, TextWriter.Null, error, _home));
            Assert.Contains("usage:", error.ToString());
        }

        [Fact]
        public void SetupMcpRegistersWithWhatIsInstalled_AndRemoveUndoesIt()
        {
            string exeDir = Path.Combine(_home, "Programs", "GpibMcp");
            Directory.CreateDirectory(exeDir);
            File.WriteAllText(Path.Combine(exeDir, "GpibMcp.exe"), "");
            Directory.CreateDirectory(Path.GetDirectoryName(VsCodeConfig));

            var output = new StringWriter();
            Assert.Equal(SetupCommands.ExitOk, SetupCommands.Run(new[] { "setup", "mcp", "--manuals", @"D:\Manuals" },
                                                                 output, TextWriter.Null, exeDir, Environment()));
            Assert.Contains("VS Code", output.ToString());
            Assert.Equal(McpClientState.Registered,
                         Client("vscode").State(new McpServerEntry(Path.Combine(exeDir, "GpibMcp.exe"), @"D:\Manuals")));

            Assert.Equal(SetupCommands.ExitOk, SetupCommands.Run(new[] { "setup", "mcp", "--remove" },
                                                                 TextWriter.Null, TextWriter.Null, exeDir, Environment()));
            Assert.False(File.Exists(VsCodeConfig));
        }

        // ---------------------------------------------------------------- the NI check

        /// <summary>A check over the real backend assembly, with NI's assemblies and DLLs faked.</summary>
        private static NiCheck.Result FakeCheck(Func<AssemblyName, Assembly> load, Func<string, bool> native,
                                                Func<string, IReadOnlyList<Version>> installed)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            return new NiCheck(exeDir, load, native, installed, () => null).Run();
        }

        private static Assembly AnyAssembly(AssemblyName name) => typeof(object).Assembly;

        [Fact]
        public void WithEverythingPresentTheCheckPasses()
        {
            NiCheck.Result result = FakeCheck(AnyAssembly, dll => true, name => new Version[0]);
            Assert.True(result.Ok);
            Assert.Contains("are installed", result.Report());
        }

        [Fact]
        public void AMissingDriverNamesTheProductToInstall()
        {
            NiCheck.Result result = FakeCheck(AnyAssembly, dll => dll != "ni4882.dll", name => new Version[0]);

            Assert.False(result.Ok);
            Assert.Equal(new[] { "NI-488.2" }, result.ProductsNeeded);
            Assert.Contains(NiCheck.Ni4882Download, result.Report());
            Assert.DoesNotContain(NiCheck.NiVisaDownload, result.Report());
        }

        [Fact]
        public void AnOlderNiVisaIsReportedAsTooOld_NotMissing()
        {
            // The case "is NI installed?" gets wrong: it is, but older than the build references.
            Func<AssemblyName, Assembly> load = name =>
            {
                if (name.Name == "NationalInstruments.Visa") throw new FileLoadException("version mismatch");
                return typeof(object).Assembly;
            };
            NiCheck.Result result = FakeCheck(load, dll => true,
                name => name == "NationalInstruments.Visa" ? new[] { new Version(1, 0, 0, 0) } : new Version[0]);

            NiCheck.Component visa = result.Components.Single(c => c.Name.Contains("NationalInstruments.Visa"));
            Assert.Equal(NiCheck.Status.TooOld, visa.Status);
            Assert.Contains("this PC has 1.0.0.0", visa.Detail);
            Assert.Equal(new[] { "NI-VISA" }, result.ProductsNeeded);
        }

        [Fact]
        public void TheBackendIsNotStartedWhenSomethingItNeedsIsMissing()
        {
            bool started = false;
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            new NiCheck(exeDir, AnyAssembly, dll => false, name => new Version[0], () => { started = true; return null; }).Run();
            Assert.False(started);
        }

        [Fact]
        public void WithoutTheBackendAssemblyTheInstallIsReportedIncomplete()
        {
            NiCheck.Result result = new NiCheck(_home, AnyAssembly, dll => true, name => new Version[0], () => null).Run();
            Assert.False(result.Ok);
            Assert.Contains("reinstall GPIB MCP", result.Report());
        }

        [Fact]
        public void OnTheBuildMachineTheRealNiAssembliesLoad()
        {
            // The build needs NI installed, so wherever these tests run the real check must pass - short of
            // starting NI-VISA, which a CI service account need not be able to do.
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            NiCheck.Result result = new NiCheck(exeDir).Run(startBackend: false);
            Assert.True(result.Ok, result.Report());
        }
    }
}
