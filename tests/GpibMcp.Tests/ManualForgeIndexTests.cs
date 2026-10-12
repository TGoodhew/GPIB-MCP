using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using GpibMcp.Manuals;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Choosing manuals by content when ManualForge has indexed the library (#134). ManualForge itself is
    /// stood in for by a batch file that prints canned paths and exits with a chosen code - the contract
    /// under test is the CLI's (paths on stdout, exit 0/2/1), and a real index would test the machine.
    ///
    /// The "absent" and "failing" cases compare against the same search with the feature switched off, so
    /// "behaves exactly as it does today" is asserted rather than approximated.
    /// </summary>
    public class ManualForgeIndexTests : IDisposable
    {
        private readonly string _root;
        private readonly string _tools;
        private readonly string _argsLog;

        public ManualForgeIndexTests()
        {
            string scratch = Path.Combine(Path.GetTempPath(), "gpibmcp-manualforge-" + Guid.NewGuid().ToString("N"));
            _root = Path.Combine(scratch, "library");
            _tools = Path.Combine(scratch, "tools");
            _argsLog = Path.Combine(_tools, "args.txt");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_tools);
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUAL_CACHE", Path.Combine(scratch, "cache"));
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUAL_CACHE", null);
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", null);
            try { Directory.Delete(Path.GetDirectoryName(_root), recursive: true); } catch { /* best effort */ }
        }

        private void Write(string relativePath, string contents)
        {
            string full = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, contents);
        }

        /// <summary>What ManualForge leaves behind after indexing; its contents are never read here.</summary>
        private void CreateIndex() => Write(ManualForgeIndex.IndexRelativePath, "SQLite format 3");

        /// <summary>
        /// A stand-in <c>manualforge</c> that records its arguments, prints <paramref name="paths"/> (relative
        /// ones are placed in the library) and exits with <paramref name="exitCode"/>.
        /// </summary>
        private void FakeManualForge(int exitCode, params string[] paths)
        {
            var script = new StringBuilder("@echo off\r\n");
            script.Append("echo %* > \"").Append(_argsLog).Append("\"\r\n");
            foreach (string path in paths)
                script.Append("echo ").Append(Path.IsPathRooted(path) ? path : Path.Combine(_root, path)).Append("\r\n");
            if (exitCode == 1) script.Append("echo No index here. 1>&2\r\n");
            script.Append("exit /b ").Append(exitCode).Append("\r\n");

            string tool = Path.Combine(_tools, "manualforge.cmd");
            File.WriteAllText(tool, script.ToString());
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", tool);
        }

        private bool ToolWasRun => File.Exists(_argsLog);

        private ToolOutput Search(JObject args)
        {
            var registry = new ToolRegistry();
            ManualTools.Register(registry, ManualLibrary.At(_root));
            registry.TryGet("manual_search", out var tool);
            return tool.Invoke(args);
        }

        /// <summary>The same search with ManualForge switched off: today's behaviour, by definition.</summary>
        private ToolOutput SearchWithoutManualForge(JObject args)
        {
            string configured = Environment.GetEnvironmentVariable("GPIB_MCP_MANUALFORGE");
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", "off");
            try { return Search(args); }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", configured); }
        }

        private static void AssertSameResult(ToolOutput expected, ToolOutput actual)
        {
            Assert.Equal(expected.AsText(), actual.AsText());
            Assert.True(JToken.DeepEquals(expected.Structured, actual.Structured),
                        "structured output differs:\n" + expected.Structured + "\n---\n" + actual.Structured);
        }

        private static List<string> Searched(ToolOutput output) =>
            ((JArray)output.Structured["searched"]).Select(f => (string)f).ToList();

        /// <summary>
        /// The issue's real example in miniature: the book named for the topic is not the manual that answers
        /// it, and only the pages know which one does.
        /// </summary>
        private void OscillatorLibrary()
        {
            Write("Troubleshooting Analog Circuits.txt", "Chapter 1\n\fOp-amp troubleshooting basics.\n");
            Write("8591E Service Guide.txt",
                  "8591E Service\n\fA22 OCXO\n Oven controlled crystal oscillator troubleshooting hints.\n");
        }

        private static JObject OscillatorQuery() => new JObject { ["query"] = "crystal oscillator troubleshooting" };

        // ---------------------------------------------------------------- absent: today's behaviour, exactly

        [Fact]
        public void WithNoIndexTheToolIsNeverRunAndNothingChanges()
        {
            OscillatorLibrary();
            FakeManualForge(0, "8591E Service Guide.txt");

            ToolOutput output = Search(OscillatorQuery());

            Assert.False(ToolWasRun);
            AssertSameResult(SearchWithoutManualForge(OscillatorQuery()), output);
            Assert.Equal(new[] { "Troubleshooting Analog Circuits.txt" }, Searched(output));
        }

        [Fact]
        public void AnIndexWithNoToolToReadItChangesNothing()
        {
            OscillatorLibrary();
            CreateIndex();
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", Path.Combine(_tools, "no-such-manualforge.exe"));

            AssertSameResult(SearchWithoutManualForge(OscillatorQuery()), Search(OscillatorQuery()));
        }

        // ---------------------------------------------------------------- present

        [Fact]
        public void WithAnIndexAndNoModelTheManualsAreChosenByWhatTheirPagesSay()
        {
            OscillatorLibrary();
            CreateIndex();
            FakeManualForge(0, "8591E Service Guide.txt");

            ToolOutput output = Search(OscillatorQuery());

            Assert.True(ToolWasRun);
            List<string> searched = Searched(output);
            Assert.Equal("8591E Service Guide.txt", searched[0]);              // content-ranked first
            Assert.Contains("Troubleshooting Analog Circuits.txt", searched);  // filename candidates still there
            Assert.Equal("8591E Service Guide.txt", (string)output.Structured["hits"].First()["file"]);
            Assert.Equal("content", (string)output.Structured["candidatesBy"]);
            Assert.Contains("chosen by content", output.AsText());
            Assert.Contains("picked 1 of the 2 opened", output.AsText());
        }

        [Fact]
        public void AModelWithFewManualsNeverPaysForTheIndex()
        {
            // The common instrument-driving query: filenames already found every manual for the model, so the
            // index could only reorder them, and it is not worth a process start to do that.
            Write("8563E Programming.txt", "8563E\n\fCF Center Frequency\n");
            CreateIndex();
            FakeManualForge(0, "8563E Programming.txt");
            var query = new JObject { ["model"] = "8563E", ["query"] = "center frequency" };

            ToolOutput output = Search(query);

            Assert.False(ToolWasRun);
            AssertSameResult(SearchWithoutManualForge(query), output);
        }

        [Fact]
        public void AModelWithMoreManualsThanTheCapLetsTheIndexChooseAmongThemOnly()
        {
            for (int i = 1; i <= ManualLibrary.MaxFilesPerSearch + 3; i++)
                Write("8563E part " + i.ToString("00") + ".txt", "8563E part " + i + "\n\fNothing relevant.\n");
            Write("8563E part 15.txt", "8563E part 15\n\fCF Center Frequency\n");
            Write("3325B Operating.txt", "3325B\n\fCenter frequency is not a 3325B term.\n");
            CreateIndex();
            // The index ranks another instrument's manual first; the caller said 8563E, so it must not displace
            // the 8563E's own manuals.
            FakeManualForge(0, "3325B Operating.txt", "8563E part 15.txt");

            ToolOutput output = Search(new JObject { ["model"] = "8563E", ["query"] = "center frequency" });

            Assert.True(ToolWasRun);
            Assert.Contains("--model 8563E", File.ReadAllText(_argsLog));
            List<string> searched = Searched(output);
            Assert.Equal("8563E part 15.txt", searched[0]);
            Assert.DoesNotContain("3325B Operating.txt", searched);
            Assert.Equal(ManualLibrary.MaxFilesPerSearch, searched.Count);   // the cap still holds
            Assert.Equal("content", (string)output.Structured["candidatesBy"]);
        }

        [Fact]
        public void WhenNoPageMatchesEitherTheResultSaysTheIndexWasAsked()
        {
            Write("3325B Operating.txt", "3325B\n\fFR Frequency\n");
            CreateIndex();
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "hyperspatial flux capacitor" });

            Assert.True(ToolWasRun);
            Assert.Empty((JArray)output.Structured["searched"]);
            Assert.Contains("no page in the library matched", output.AsText());
            Assert.Null(output.Structured["candidatesBy"]);
        }

        // ---------------------------------------------------------------- failing: fall back, silently

        [Fact]
        public void AnIndexThatCannotBeSearchedFallsBackToFilenames()
        {
            OscillatorLibrary();
            CreateIndex();
            FakeManualForge(1);

            ToolOutput output = Search(OscillatorQuery());

            Assert.True(ToolWasRun);
            AssertSameResult(SearchWithoutManualForge(OscillatorQuery()), output);
        }

        [Fact]
        public void PathsOutsideTheLibraryOrNoLongerThereAreIgnored()
        {
            // A stale or moved index must not open files it has no business opening, nor fail on ones that
            // have gone; with nothing usable left, filenames decide as they always did.
            OscillatorLibrary();
            CreateIndex();
            string outside = Path.Combine(_tools, "elsewhere.txt");
            File.WriteAllText(outside, "crystal oscillator troubleshooting");
            FakeManualForge(0, outside, "deleted since indexing.pdf");

            ToolOutput output = Search(OscillatorQuery());

            Assert.True(ToolWasRun);
            AssertSameResult(SearchWithoutManualForge(OscillatorQuery()), output);
        }

        [Fact]
        public void TheFeatureCanBeSwitchedOff()
        {
            CreateIndex();
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", "off");
            Assert.Null(ManualForgeIndex.FindTool());
            Assert.False(ManualForgeIndex.IsAvailable(_root));
        }

        // ---------------------------------------------------------------- the command line

        [Theory]
        [InlineData("center frequency", "\"center frequency\"")]
        [InlineData("OUTPPLOT", "OUTPPLOT")]
        [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
        [InlineData(@"C:\My Manuals\", "\"C:\\My Manuals\\\\\"")]   // a trailing '\' must not escape the quote
        [InlineData(@"a\b c", "\"a\\b c\"")]                        // backslashes elsewhere are literal
        [InlineData("", "\"\"")]
        public void ArgumentsAreQuotedTheWayTheyWillBeSplit(string argument, string expected)
        {
            Assert.Equal(expected, ManualForgeIndex.QuoteArgument(argument));
        }
    }
}
