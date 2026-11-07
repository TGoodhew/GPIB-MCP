using System;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Manuals;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Searching a local folder of instrument manuals (#120). The fixtures are text files rather than PDFs
    /// on purpose: the search, the ranking and the citations are what these tests are about, and a test that
    /// needed Poppler installed would be testing the machine instead of the code.
    /// </summary>
    [Collection("ManualLibraryEnvironment")]
    public class ManualSearchTests : IDisposable
    {
        private readonly string _root;
        private readonly string _cache;

        public ManualSearchTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "gpibmcp-manuals-" + Guid.NewGuid().ToString("N"));
            _cache = Path.Combine(_root, "_cache");
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_cache);
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUAL_CACHE", _cache);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUAL_CACHE", null);
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALS", null);
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        private string Write(string relativePath, string contents)
        {
            string full = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, contents);
            return full;
        }

        /// <summary>Two pages of an 8563E-ish manual, separated the way pdftotext separates pages.</summary>
        private const string AnalyzerManual =
            "HP 8563E Programming Manual\nIntroduction to remote operation.\n" +
            "\f" +
            "CF  Center Frequency\n" +
            "    Syntax: CF <number><unit>\n" +
            "    Sets the center frequency of the displayed span.\n" +
            "\f" +
            "SP  Span\n    Syntax: SP <number><unit>\n";

        private static McpTool Tool(ManualLibrary library)
        {
            var registry = new ToolRegistry();
            ManualTools.Register(registry, library);
            registry.TryGet("manual_search", out var tool);
            return tool;
        }

        // ---------------------------------------------------------------- registration

        [Fact]
        public void TheToolIsAbsentUntilALibraryIsConfigured()
        {
            // A server with no library must not advertise a tool that could only ever answer "not configured".
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALS", null);
            var registry = InstrumentTools.BuildRegistry(new FakeInstrumentManager());
            Assert.False(registry.TryGet("manual_search", out _));

            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALS", _root);
            Assert.True(InstrumentTools.BuildRegistry(new FakeInstrumentManager()).TryGet("manual_search", out _));
        }

        [Fact]
        public void AConfiguredFolderThatDoesNotExistDisablesTheToolRatherThanFailing()
        {
            Environment.SetEnvironmentVariable("GPIB_MCP_MANUALS",
                Path.Combine(_root, "no-such-folder"));
            Assert.False(InstrumentTools.BuildRegistry(new FakeInstrumentManager()).TryGet("manual_search", out _));
        }

        // ---------------------------------------------------------------- searching

        [Fact]
        public void APassageIsFoundAndCitedByFileAndPage()
        {
            Write("8563E Programming.txt", AnalyzerManual);
            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "8563E", ["query"] = "center frequency CF" });

            JToken hit = output.Structured["hits"].First();
            Assert.Equal("8563E Programming.txt", (string)hit["file"]);
            Assert.Equal(2, (int)hit["page"]);                       // the CF page, not page 1
            Assert.Contains("Center Frequency", (string)hit["text"]);
            Assert.Contains("page 2", output.AsText());              // the citation a reader sees
        }

        [Fact]
        public void TheModelNarrowsWhichManualsAreOpened()
        {
            Write("8563E Programming.txt", AnalyzerManual);
            Write("3325B Operating.txt", "3325B\n\fFR Frequency\n Sets the output frequency.\n");

            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "3325B", ["query"] = "frequency" });

            var searched = ((JArray)output.Structured["searched"]).Select(f => (string)f).ToList();
            Assert.Contains("3325B Operating.txt", searched);
            Assert.DoesNotContain("8563E Programming.txt", searched);
        }

        [Fact]
        public void AModelFolderMatchesAsWellAsAFilename()
        {
            // Libraries are organised both ways: a file per model, or a folder per model.
            Write(Path.Combine("3458A", "operating.txt"), "3458A\n\fTRIG Trigger\n Arms the multimeter.\n");
            Write("unrelated.txt", "Nothing to do with it.");

            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "3458A", ["query"] = "trigger" });

            Assert.Equal(Path.Combine("3458A", "operating.txt"),
                         (string)output.Structured["hits"].First()["file"]);
        }

        [Fact]
        public void TheRarestWordAnchorsTheSearch()
        {
            // "frequency" appears on every page; "CF" on one. Anchoring on the rare word is what stops a
            // search returning the whole manual.
            Write("8563E.txt", AnalyzerManual);
            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "8563E", ["query"] = "CF frequency" });

            Assert.All((JArray)output.Structured["hits"], h => Assert.Contains("CF", (string)h["text"]));
        }

        [Fact]
        public void NoPassageMatchesInAManualThatWasSearched()
        {
            Write("8563E.txt", AnalyzerManual);
            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "8563E", ["query"] = "hyperspatial flux capacitor" });

            Assert.True((bool)output.Structured["ok"]);
            Assert.Empty((JArray)output.Structured["hits"]);
            Assert.NotEmpty((JArray)output.Structured["searched"]);   // it really did look
            Assert.Contains("No passage matched", output.AsText());
        }

        [Fact]
        public void NoManualNamedForTheModelSearchesNothingAndSaysWhatIsThere()
        {
            // Reading whichever files happened to be smallest would report "searched 12, nothing found",
            // which reads as "your library does not have this" when the truth is "I never opened the right
            // file". Say nothing matched, and show what is there so the next call can aim.
            Write("3325B Operating.txt", "3325B\n\fFR Frequency\n");
            Write("readme.txt", "not a manual");

            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "54622D", ["query"] = "timebase" });

            Assert.True((bool)output.Structured["ok"]);
            Assert.Empty((JArray)output.Structured["searched"]);
            Assert.Empty((JArray)output.Structured["hits"]);
            Assert.NotEmpty((JArray)output.Structured["available"]);
            Assert.Contains("nothing was searched", output.AsText());
        }

        [Fact]
        public void AZeroByteFileIsNotAManual()
        {
            File.WriteAllBytes(Path.Combine(_root, "8563E truncated download.pdf"), new byte[0]);
            Assert.Empty(ManualLibrary.At(_root).Candidates("8563E", "anything"));
        }

        [Fact]
        public void AModelFallsBackToItsSeriesManual_AndSaysSo()
        {
            // The case the real library exposed: an 8563E's programming manual is filed as "8560E
            // Programming Guide" - the series, not the instrument. A human would reach for it too, so we
            // do, at a much lower score, and the substitution is stated rather than hidden.
            Write("8560E Programming Guide.txt", AnalyzerManual);

            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "8563E", ["query"] = "center frequency CF" });

            Assert.NotEmpty((JArray)output.Structured["hits"]);
            Assert.True((bool)output.Structured["familyMatchOnly"]);
            Assert.Contains("same", output.AsText());
            Assert.Contains("series", output.AsText());
        }

        [Fact]
        public void AnExactModelMatchBeatsItsSeries()
        {
            Write("8560E Programming Guide.txt", AnalyzerManual);
            Write("8563E Programming.txt", AnalyzerManual);

            ToolOutput output = Tool(ManualLibrary.At(_root))
                .Invoke(new JObject { ["model"] = "8563E", ["query"] = "center frequency CF" });

            Assert.Equal("8563E Programming.txt", (string)output.Structured["hits"].First()["file"]);
            Assert.Null(output.Structured["familyMatchOnly"]);
        }

        [Theory]
        [InlineData("8563E", "856")]
        [InlineData("3458A", "345")]
        [InlineData("DS1054Z", null)]   // no leading digits: no family to guess at
        [InlineData("E4438C", null)]
        [InlineData("42", null)]        // too short to identify anything
        public void TheFamilyStemIsTheLeadingDigits(string model, string expected)
        {
            Assert.Equal(expected, ManualLibrary.FamilyStem(model));
        }

        [Fact]
        public void AnUnreadableManualIsReportedNotSwallowed()
        {
            // A PDF nobody can extract must never look like a PDF with no match - otherwise the tool quietly
            // reports "not in your manuals" when it means "I could not look".
            File.WriteAllBytes(Path.Combine(_root, "8563E scan.pdf"), new byte[] { 0x25, 0x50, 0x44, 0x46 });
            Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", Path.Combine(_root, "no-such-tool.exe"));
            try
            {
                ToolOutput output = Tool(ManualLibrary.At(_root))
                    .Invoke(new JObject { ["model"] = "8563E", ["query"] = "center frequency" });

                var unreadable = (JArray)output.Structured["unreadable"];
                Assert.Single(unreadable);
                Assert.Equal("8563E scan.pdf", (string)unreadable[0]["file"]);
                Assert.Contains("extraction failed", (string)unreadable[0]["problem"]);
                Assert.Contains("Could not read", output.AsText());
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
        }

        /// <summary>A real PDF, in a fixed-pitch font so a column's position in the text can be tested.</summary>
        private void WritePdf(string name, params string[][] pages)
        {
            var builder = new PdfDocumentBuilder();
            PdfDocumentBuilder.AddedFont font = builder.AddStandard14Font(Standard14Font.Courier);
            foreach (string[] lines in pages)
            {
                PdfPageBuilder page = builder.AddPage(PageSize.A4);
                for (int i = 0; i < lines.Length; i++)
                    foreach (var column in lines[i].Split('|').Select((text, n) => new { text, n }))
                        if (column.text.Length > 0)
                            page.AddText(column.text, 10, new PdfPoint(50 + column.n * 200, 750 - i * 14), font);
            }
            File.WriteAllBytes(Path.Combine(_root, name), builder.Build());
        }

        [Fact]
        public void APdfIsReadWithNothingElseInstalled_AndCitesTheRightPage()
        {
            WritePdf("8563E guide.pdf", new[] { "Introduction" }, new[] { "CF sets the center frequency" });
            Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", Path.Combine(_root, "no-such-tool.exe"));
            try
            {
                ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject { ["query"] = "center frequency", ["model"] = "8563E" });

                var hit = (JObject)Assert.Single((JArray)output.Structured["hits"]);
                Assert.Equal(2, (int)hit["page"]);
                Assert.Contains("center frequency", (string)hit["text"]);
                Assert.Empty((JArray)output.Structured["unreadable"]);
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
        }

        [Fact]
        public void AQueryWordTheManualDoesntHave_DoesntLoseTheWordsItDoes()
        {
            // The real query: "palette" appears nowhere in the 54845A's guide, and that hid everything else.
            Write("8563E guide.txt", AnalyzerManual);

            ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject { ["query"] = "center frequency palette", ["model"] = "8563E" });

            Assert.Equal(2, (int)Assert.Single((JArray)output.Structured["hits"])["page"]);
        }

        [Fact]
        public void TheBuiltInReaderKeepsATablesColumns()
        {
            WritePdf("table.pdf", new[] { "CF|center frequency|Hz", "SP|span|Hz" });

            string text = BuiltInPdfText.Read(Path.Combine(_root, "table.pdf"), 60000);

            string[] lines = text.Split('\n');
            Assert.Equal(lines[0].IndexOf("center"), lines[1].IndexOf("span"));
            Assert.Equal(lines[0].LastIndexOf("Hz"), lines[1].LastIndexOf("Hz"));
            Assert.True(lines[0].IndexOf("center") > "CF ".Length, lines[0]);
        }

        [Fact]
        public void TheBuiltInReaderSeparatesPagesLikePdftotext_AndStopsAtItsLimit()
        {
            WritePdf("two pages.pdf", new[] { "one" }, new string[0], new[] { "three" });
            string path = Path.Combine(_root, "two pages.pdf");

            string text = BuiltInPdfText.Read(path, 60000);
            Assert.Equal(3, ManualText.PageAt(text, text.IndexOf("three")));

            var ex = Assert.Throws<TimeoutException>(() => BuiltInPdfText.Read(path, -1));
            Assert.Contains("did not finish", ex.Message);
        }

        [Fact]
        public void PdftotextStillWins_WhenItIsThere()
        {
            WritePdf("8563E guide.pdf", new[] { "What the built-in reader would see" });
            string tool = Path.Combine(_root, "pdftotext.cmd");
            File.WriteAllText(tool, "@echo off\r\necho The extractor's CF text> %4\r\n");
            Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", tool);
            try
            {
                ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject { ["query"] = "CF", ["model"] = "8563E" });

                Assert.Contains("extractor's", (string)Assert.Single((JArray)output.Structured["hits"])["text"]);
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
        }

        [Fact]
        public void APdfWithNoTextLayer_SaysItNeedsOcr_RatherThanLookingLikeNoMatch()
        {
            WritePdf("8563E scan.pdf", new string[0]);
            Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", Path.Combine(_root, "no-such-tool.exe"));
            try
            {
                ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject { ["query"] = "CF", ["model"] = "8563E" });

                string problem = (string)Assert.Single((JArray)output.Structured["unreadable"])["problem"];
                Assert.Contains("OCR", problem);
                Assert.Contains("ManualForge", problem);
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
        }

        [Fact]
        public void ASearchOutOfTime_ListsTheManualsItDidntReach_RatherThanSayingTheyDontMatch()
        {
            Write("8563E one.txt", AnalyzerManual);
            Write("8563E two.txt", AnalyzerManual);
            var files = ManualLibrary.At(_root).Candidates("8563E", "CF");

            ManualSearch.Results results = ManualSearch.Run(files, "CF", timeBudgetMs: 0);

            Assert.Empty(results.Searched);
            Assert.Equal(2, results.Unreadable.Count);
            Assert.All(results.Unreadable, note => Assert.Contains("time ran out", note.Problem));
        }

        [Fact]
        public void AManualStillBeingRead_IsSaidToBe_AndTheNextSearchJoinsTheSameRead()
        {
            Write("8563E big.txt", AnalyzerManual);
            var files = ManualLibrary.At(_root).Candidates("8563E", "CF");
            var release = new System.Threading.ManualResetEventSlim();
            int reads = 0;
            Func<ManualFile, ManualText.Result> original = ManualSearch.ReadText;
            ManualSearch.ReadText = f =>
            {
                System.Threading.Interlocked.Increment(ref reads);
                release.Wait(10000);
                return new ManualText.Result(ManualText.Outcome.Extracted, AnalyzerManual, null);
            };
            try
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                ManualSearch.Results first = ManualSearch.Run(files, "CF", timeBudgetMs: 300);
                Assert.True(clock.ElapsedMilliseconds < 3000, clock.ElapsedMilliseconds + " ms");
                Assert.Contains("still being read", Assert.Single(first.Unreadable).Problem);
                Assert.Empty(first.Searched);

                ManualSearch.Results second = ManualSearch.Run(files, "CF", timeBudgetMs: 300);
                Assert.Contains("still being read", Assert.Single(second.Unreadable).Problem);
                Assert.Equal(1, reads);

                release.Set();
                ManualSearch.Results third = ManualSearch.Run(files, "CF", timeBudgetMs: 5000);
                Assert.Empty(third.Unreadable);
                Assert.Equal(2, third.Hits.Single().Page);
            }
            finally
            {
                release.Set();
                ManualSearch.ReadText = original;
            }
        }

        [Fact]
        public void PagesThatCouldntBeRead_AreNamed_AndTheRestIsStillSearched()
        {
            Write("8563E guide.txt", AnalyzerManual);
            var files = ManualLibrary.At(_root).Candidates("8563E", "CF");
            Func<ManualFile, ManualText.Result> original = ManualSearch.ReadText;
            string damaged = "Intro\f" + ManualText.UnreadablePage + "\fCF Center Frequency\f" + ManualText.UnreadablePage;
            ManualSearch.ReadText = f => new ManualText.Result(ManualText.Outcome.Extracted, damaged, null);
            try
            {
                ManualSearch.Results results = ManualSearch.Run(files, "CF");

                Assert.Equal(3, results.Hits.Single().Page);
                Assert.Single(results.Searched);
                Assert.Equal("pages 2 and 4 could not be read, so they weren't searched. The rest of the manual was.",
                             Assert.Single(results.Unreadable).Problem);

                ManualSearch.ReadText = f => new ManualText.Result(ManualText.Outcome.Extracted,
                    ManualText.UnreadablePage + "\f" + ManualText.UnreadablePage, null);
                results = ManualSearch.Run(files, "CF");
                Assert.Empty(results.Searched);
                Assert.Contains("none of its 2 page(s)", Assert.Single(results.Unreadable).Problem);
            }
            finally { ManualSearch.ReadText = original; }
        }

        [Fact]
        public void ASidecarTextFileIsUsedWhenThereIsNoExtractor()
        {
            // The route for a user with no Poppler: extract once, however they like, leave the .txt beside it.
            File.WriteAllBytes(Path.Combine(_root, "8563E manual.pdf"), new byte[] { 0x25, 0x50, 0x44, 0x46 });
            Write("8563E manual.txt", AnalyzerManual);

            Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", Path.Combine(_root, "no-such-tool.exe"));
            try
            {
                ToolOutput output = Tool(ManualLibrary.At(_root))
                    .Invoke(new JObject { ["model"] = "8563E", ["query"] = "center frequency CF" });

                Assert.NotEmpty((JArray)output.Structured["hits"]);
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
        }

        // ---------------------------------------------------------------- safety and shape

        [Fact]
        public void APathOutsideTheLibraryIsRefused()
        {
            // The path comes from a tool argument, so traversal has to bounce off something.
            Write("8563E.txt", AnalyzerManual);
            ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject
            {
                ["query"] = "anything",
                ["file"] = Path.Combine("..", "..", "windows", "win.ini")
            });

            Assert.True(output.IsError);
            Assert.False((bool)output.Structured["ok"]);
        }

        [Fact]
        public void AnEmptyLibrarySaysSoRatherThanPretendingToSearch()
        {
            ToolOutput output = Tool(ManualLibrary.At(_root)).Invoke(new JObject { ["query"] = "anything" });
            Assert.Empty((JArray)output.Structured["searched"]);
            Assert.Contains("0 manual(s) in the library", output.AsText());
        }

        [Fact]
        public void TheToolDeclaresItsOutputSchema()
        {
            Assert.NotNull(Tool(ManualLibrary.At(_root)).OutputSchema);
            Assert.Equal("object", (string)Tool(ManualLibrary.At(_root)).OutputSchema["type"]);
        }

        [Fact]
        public void ExtractedTextIsCachedSoAManualIsConvertedOnce()
        {
            Write("8563E.txt", AnalyzerManual);
            var file = ManualLibrary.At(_root).Candidates("8563E", "center frequency").First();

            Assert.True(ManualText.Read(file).Ok);
            Assert.Contains("Center Frequency", ManualText.Read(file).Text);
        }

        [Fact]
        public void PageNumbersCountFromOne()
        {
            Assert.Equal(1, ManualText.PageAt("no breaks here", 5));
            Assert.Equal(2, ManualText.PageAt("one\ftwo", 5));
            Assert.Equal(3, ManualText.PageAt("one\ftwo\fthree", 9));
        }
    }
}
