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
    [Collection("ManualLibraryEnvironment")]   // it sets the same process-wide variables as ManualSearchTests
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

            // A page search, answered from PagesReply if a test set one. Logged apart from the file search, so
            // what the tests below say about choosing manuals still means that.
            string json = Path.Combine(_tools, "pages.json");
            string pagesLog = Path.Combine(_tools, "pages-args.txt");
            script.Append("echo %* | findstr /c:\"--json\" >nul && (echo %* > \"").Append(pagesLog).Append("\"")
                  .Append(" & if exist \"").Append(json).Append("\" (type \"").Append(json).Append("\" & exit /b 0) else exit /b 2)\r\n");
            if (PagesReply != null) File.WriteAllText(json, PagesReply);

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

        private bool PagesWereAskedFor => File.Exists(Path.Combine(_tools, "pages-args.txt"));

        /// <summary>What <c>search --json</c> prints, or null to have it match nothing.</summary>
        private string PagesReply;

        /// <summary>A real reply's shape, as <c>manualforge search --json</c> printed it for this library.</summary>
        private string Pages(params object[][] hits) => new JObject
        {
            ["schemaVersion"] = 1,
            ["results"] = new JArray(hits.Select(h => (object)new JObject
            {
                ["path"] = Path.Combine(_root, (string)h[0]),
                ["title"] = Path.GetFileNameWithoutExtension((string)h[0]),
                ["page"] = (int)h[1],
                ["snippet"] = (string)h[2],
                ["recoveredByOcr"] = (bool)h[3]
            }).ToArray())
        }.ToString();

        // ---- pages from the index ----------------------------------------------------------------------

        [Fact]
        public void TheCopiesManualForgeSetAside_AreNotSearchedAgain()
        {
            Write(Path.Combine("_Originals", "8563E Guide.txt"), "The CF command.\n");
            Write("8563E Guide.txt", "The CF command.\n");
            CreateIndex();
            FakeManualForge(2);

            Assert.Equal(new[] { "8563E Guide.txt" }, Searched(Search(new JObject { ["query"] = "CF", ["model"] = "8563E" })));
        }

        [Fact]
        public void AFolderOfThatNameIsAnOrdinaryFolder_WithoutManualForgesIndex()
        {
            Write(Path.Combine("_Originals", "8563E Guide.txt"), "The CF command.\n");

            Assert.Single(Searched(Search(new JObject { ["query"] = "CF", ["model"] = "8563E" })));
        }

        [Fact]
        public void APageOnlyTheIndexCanRead_IsReturned_AndSaysWhereItCameFrom()
        {
            // The real case: the 54845A's syntax diagrams are pictures, so the PDF holds no words for them.
            Write("54845A Programmer.txt", "SYSTem commands, and nothing else.\n");
            CreateIndex();
            PagesReply = Pages(new object[] { "54845A Programmer.txt", 46, ":DISPlay [DATA]? BMP PCX GIF", true });
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "DATA?", ["model"] = "54845A" });

            var hit = (JObject)Assert.Single((JArray)output.Structured["hits"]);
            Assert.Equal("54845A Programmer.txt", (string)hit["file"]);
            Assert.Equal(46, (int)hit["page"]);
            Assert.Equal("manualforge", (string)hit["source"]);
            Assert.True((bool)hit["ocr"]);
            Assert.Contains("page 46 (ManualForge, recovered by OCR)", output.AsText());
            Assert.Contains("read_manual_page", output.AsText());
        }

        [Fact]
        public void IndexPagesFillWhatReadingLeft_WithoutRepeatingAPageItFound()
        {
            Write("8563E Guide.txt", "Page one.\n\fThe CF command sets centre frequency.\n");
            CreateIndex();
            PagesReply = Pages(new object[] { "8563E Guide.txt", 2, "The [CF] command", false },
                               new object[] { "8563E Guide.txt", 9, "[CF] in a table", false });
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "CF", ["model"] = "8563E" });

            var hits = (JArray)output.Structured["hits"];
            Assert.Equal(new[] { 2, 9 }, hits.Select(h => (int)h["page"]).ToArray());
            Assert.Null(hits[0]["source"]);                        // the passage read from the file wins
            Assert.Equal("manualforge", (string)hits[1]["source"]);
            Assert.Null(hits[1]["ocr"]);
        }

        [Fact]
        public void APageWithEveryWord_BeatsPassagesWithOnlySomeOfThem()
        {
            // The real case: the file names GIF on page after page, while the one page giving the syntax is a
            // picture only the index can read. Passages that merely fill the slots mustn't crowd it out.
            Write("54845A Guide.txt", "GIF one.\n\fGIF two.\n\fGIF three.\n");
            CreateIndex();
            PagesReply = Pages(new object[] { "54845A Guide.txt", 46, "[GIF] [TIF] [GRATicule]", true });
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "GIF TIF GRATicule", ["model"] = "54845A", ["max_results"] = 2 });

            var pages = ((JArray)output.Structured["hits"]).Select(h => (int)h["page"]).ToArray();
            Assert.Equal(46, pages[0]);
            Assert.Equal(2, pages.Length);
        }

        [Fact]
        public void AnIndexSnippetReplacesAPassageThatMatchedOnlyPartOfTheQuery_OnTheSamePage()
        {
            // The file's text for page 2 has GIF but not the rest, which only OCR recovered.
            Write("54845A Guide.txt", "Intro.\n\fGIF only.\n");
            CreateIndex();
            PagesReply = Pages(new object[] { "54845A Guide.txt", 2, "[GIF] [TIF] [GRATicule]", true });
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "GIF TIF GRATicule", ["model"] = "54845A" });

            var hit = (JObject)Assert.Single((JArray)output.Structured["hits"]);
            Assert.Equal(2, (int)hit["page"]);
            Assert.Equal("manualforge", (string)hit["source"]);
        }

        [Fact]
        public void ASearchOfOneFile_AsksTheIndexForEnoughPagesToReachThatFile()
        {
            // The index can't be narrowed to a file, so the library's best pages would fill a short reply.
            Write("8563E Guide.txt", "Nothing relevant.\n");
            CreateIndex();
            FakeManualForge(2);

            Search(new JObject { ["query"] = "CF", ["model"] = "8563E", ["file"] = "8563E Guide.txt" });

            Assert.Contains("--limit " + ManualLibrary.PagesForOneFile, File.ReadAllText(Path.Combine(_tools, "pages-args.txt")));
        }

        [Fact]
        public void AnIndexLookupGivesUpAtTheTimeItWasGiven()
        {
            CreateIndex();
            string tool = Path.Combine(_tools, "manualforge.cmd");
            File.WriteAllText(tool, "@echo off\r\nping -n 6 127.0.0.1 >nul\r\nexit /b 2\r\n");
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALFORGE", tool);

            var clock = System.Diagnostics.Stopwatch.StartNew();
            ManualForgeIndex.Result result = ManualForgeIndex.SearchPages(_root, "CF", null, 5, timeoutMs: 500);

            Assert.Equal(ManualForgeIndex.Outcome.Failed, result.Outcome);
            Assert.True(clock.ElapsedMilliseconds < 3000, clock.ElapsedMilliseconds + " ms");
        }

        [Fact]
        public void ASearchTheManualsAnswerInFull_NeverStartsTheProcess()
        {
            Write("8563E Guide.txt", "CF one.\n\fCF two.\n");
            CreateIndex();
            FakeManualForge(0);

            Search(new JObject { ["query"] = "CF", ["model"] = "8563E", ["max_results"] = 2 });

            Assert.False(PagesWereAskedFor);
        }

        [Fact]
        public void IndexPagesKeepToTheCallersModelAndFile_AndStayInsideTheLibrary()
        {
            Write("8563E Guide.txt", "Nothing relevant.\n");
            Write("8563E Extra.txt", "Nothing relevant.\n");
            Write("3458A Manual.txt", "Nothing relevant.\n");
            CreateIndex();
            PagesReply = Pages(new object[] { "3458A Manual.txt", 4, "another instrument", false },
                               new object[] { "8563E Extra.txt", 5, "another file", false },
                               new object[] { "8563E Guide.txt", 6, "the right one", false },
                               new object[] { Path.Combine("..", "outside.txt"), 7, "outside the library", false });
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "relevant?", ["model"] = "8563E", ["file"] = "8563E Guide.txt" });

            Assert.Equal(6, (int)Assert.Single((JArray)output.Structured["hits"])["page"]);
        }

        [Theory]
        [InlineData("not json at all")]
        [InlineData("{\"schemaVersion\": 2, \"results\": [{\"path\": \"x\", \"page\": 1, \"snippet\": \"y\"}]}")]
        public void AnIndexReplyThisBuildCantRead_ChangesNothing(string reply)
        {
            Write("8563E Guide.txt", "The CF command.\n");
            CreateIndex();
            PagesReply = reply;
            FakeManualForge(2);

            ToolOutput output = Search(new JObject { ["query"] = "CF", ["model"] = "8563E" });

            Assert.True(PagesWereAskedFor);
            AssertSameResult(SearchWithoutManualForge(new JObject { ["query"] = "CF", ["model"] = "8563E" }), output);
        }

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
