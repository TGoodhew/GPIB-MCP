using System;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using GpibMcp.Manuals;
using GpibMcp.Mcp;
using GpibMcp.Tools;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Searching a local folder of instrument manuals (#120). The fixtures are text files rather than PDFs
    /// on purpose: the search, the ranking and the citations are what these tests are about, and a test that
    /// needed Poppler installed would be testing the machine instead of the code.
    /// </summary>
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
                Assert.Contains("pdftotext", (string)unreadable[0]["problem"]);
                Assert.Contains("Could not read", output.AsText());
            }
            finally { Environment.SetEnvironmentVariable("GPIB_MCP_PDFTOTEXT", null); }
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
