using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Manuals
{
    /// <summary>
    /// ManualForge's full-text index, consulted when it happens to be installed (#134): which manuals to open,
    /// and the pages reading them can't find, because it holds text recovered by OCR that no PDF does.
    ///
    /// <see cref="ManualLibrary"/> picks which manuals to open by filename, and a filename cannot name a topic:
    /// "crystal oscillator troubleshooting" lives in service manuals whose names say nothing of the kind.
    /// ManualForge (github.com/TGoodhew/ManualForge) OCRs a library and indexes every page of it, so when it
    /// has indexed this library it can say which files discuss the question.
    ///
    /// It is an enhancement this server detects, never a dependency it takes. Reading ManualForge's SQLite
    /// file directly would need a SQLite driver in a server whose shape is "no external dependencies", so this
    /// does what <see cref="ManualText"/> does for <c>pdftotext</c>: run the tool if it is there, and fall back
    /// quietly when it is not, fails, or takes too long. The contract it relies on is the CLI's -
    /// <c>manualforge search &lt;query&gt; --library &lt;root&gt; --files-only</c> prints ranked distinct
    /// paths, one per line, and exits 0 with results, 2 when nothing matched, 1 when it could not search.
    /// </summary>
    public static class ManualForgeIndex
    {
        /// <summary>Where ManualForge keeps a library's index, relative to the library root.</summary>
        public static readonly string IndexRelativePath = Path.Combine("_Originals", "manualforge-index.db");

        /// <summary>
        /// The search is bounded like extraction is. A warm query takes ~150 ms; the first against an index on
        /// a synced or sleeping drive has been measured at 9 s, and that is still worth waiting for when the
        /// alternative is opening twelve manuals chosen by name. A caller with less time left passes less.
        /// </summary>
        public const int SearchTimeoutMs = 20000;

        /// <summary>The <c>search --json</c> reply this reads, from its <c>schemaVersion</c>.</summary>
        private const int SearchSchemaVersion = 1;

        /// <summary>Exit codes from ManualForge's search command - part of its documented contract.</summary>
        private const int ExitNothingMatched = 2;

        public enum Outcome
        {
            /// <summary>No index for this library, or no tool to read it with. The ordinary case.</summary>
            NotAvailable,
            Found,
            NothingMatched,
            Failed
        }

        /// <summary>One page ManualForge matched.</summary>
        public sealed class PageHit
        {
            /// <summary>Full path of the manual.</summary>
            public string Path { get; set; }

            /// <summary>1-based page.</summary>
            public int Page { get; set; }

            /// <summary>The matched words in context, with [brackets] round them.</summary>
            public string Snippet { get; set; }

            /// <summary>True when the text came from ManualForge's OCR rather than the PDF's own text layer.</summary>
            public bool RecoveredByOcr { get; set; }
        }

        public sealed class Result
        {
            public Result(Outcome outcome, IReadOnlyList<string> paths, string detail, IReadOnlyList<PageHit> pages = null)
            {
                Outcome = outcome;
                Paths = paths ?? new string[0];
                Detail = detail;
                Pages = pages ?? new PageHit[0];
            }

            public Outcome Outcome { get; }

            /// <summary>Full paths, best first, as ManualForge ranked them.</summary>
            public IReadOnlyList<string> Paths { get; }

            /// <summary>Pages, best first, as ManualForge ranked them.</summary>
            public IReadOnlyList<PageHit> Pages { get; }

            /// <summary>Why there are no paths, when there are none.</summary>
            public string Detail { get; }
        }

        /// <summary>The index file for a library, whether or not it exists.</summary>
        public static string IndexPath(string root) => Path.Combine(root, IndexRelativePath);

        /// <summary>
        /// True when this library has been indexed and there is a tool to ask. Both are checked before any
        /// process is started, so a library ManualForge has never seen costs nothing at all.
        /// </summary>
        public static bool IsAvailable(string root) =>
            !string.IsNullOrWhiteSpace(root) && File.Exists(IndexPath(root)) && FindTool() != null;

        /// <summary>
        /// The <c>manualforge</c> executable, or null. <c>GPIB_MCP_MANUALFORGE</c> names it explicitly, or turns
        /// the feature off with <c>off</c>; otherwise PATH is searched, where ManualForge's installer puts it.
        /// </summary>
        public static string FindTool()
        {
            string configured = Environment.GetEnvironmentVariable("GPIB_MCP_MANUALFORGE");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string value = configured.Trim().Trim('"');
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
                    value.Equals("none", StringComparison.OrdinalIgnoreCase) ||
                    value == "0")
                    return null;
                return File.Exists(value) ? value : null;
            }

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim().Trim('"'), "manualforge.exe"); }
                catch (Exception) { continue; }   // a malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        /// <summary>
        /// Asks the index which files in <paramref name="root"/> best answer <paramref name="query"/>. Never
        /// throws: anything that goes wrong is an outcome, because every one of them means "use filenames".
        /// </summary>
        public static Result Search(string root, string query, string model, int limit, int timeoutMs = SearchTimeoutMs) =>
            Ask(root, query, model, limit, "--files-only", ParsePaths, timeoutMs);

        /// <summary>
        /// Asks the index which pages in <paramref name="root"/> match <paramref name="query"/>. Never throws, as
        /// <see cref="Search"/> doesn't: anything that goes wrong just means no pages from here.
        /// </summary>
        public static Result SearchPages(string root, string query, string model, int limit, int timeoutMs = SearchTimeoutMs) =>
            Ask(root, query, model, limit, "--json", ParsePages, timeoutMs);

        private static Result Ask(string root, string query, string model, int limit, string format, Func<byte[], Result> parse,
                                  int timeoutMs)
        {
            timeoutMs = Math.Max(1, timeoutMs);
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(query) || !File.Exists(IndexPath(root)))
                return new Result(Outcome.NotAvailable, null, "this library has no ManualForge index");

            string tool = FindTool();
            if (tool == null)
                return new Result(Outcome.NotAvailable, null, "the library is indexed but 'manualforge' is not on PATH");

            var arguments = new StringBuilder("search ")
                .Append(QuoteArgument(query))
                .Append(" --library ").Append(QuoteArgument(root))
                .Append(' ').Append(format).Append(" --limit ").Append(Math.Max(1, limit));
            // A bias, not a filter, on ManualForge's side; the caller still applies its own model filter.
            if (!string.IsNullOrWhiteSpace(model))
                arguments.Append(" --model ").Append(QuoteArgument(model.Trim()));

            try
            {
                var psi = new ProcessStartInfo(tool, arguments.ToString())
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return Failed("could not start '" + tool + "'");

                    // Both streams are drained concurrently: a child blocked writing to a full stderr pipe
                    // would otherwise never exit, and the timeout below would be the only thing to notice.
                    var stdout = new MemoryStream();
                    Task copyOut = process.StandardOutput.BaseStream.CopyToAsync(stdout);
                    Task<string> readErr = process.StandardError.ReadToEndAsync();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        return Failed("no answer within " + (timeoutMs / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + " s");
                    }
                    Task.WaitAll(new Task[] { copyOut, readErr }, timeoutMs);

                    if (process.ExitCode == ExitNothingMatched)
                        return new Result(Outcome.NothingMatched, null, "no page in the index matched");

                    if (process.ExitCode != 0)
                    {
                        string stderr = readErr.IsCompleted ? readErr.Result.Trim() : "";
                        return Failed("exit " + process.ExitCode + (stderr.Length == 0 ? "" : ": " + stderr));
                    }

                    return parse(stdout.ToArray());
                }
            }
            catch (Exception ex)
            {
                return Failed(ex.Message);
            }
        }

        private static Result ParsePaths(byte[] stdout)
        {
            List<string> paths = Decode(stdout)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .ToList();

            return paths.Count == 0
                ? new Result(Outcome.NothingMatched, null, "the index returned no paths")
                : new Result(Outcome.Found, paths, null);
        }

        private static Result ParsePages(byte[] stdout)
        {
            JObject reply = JObject.Parse(Decode(stdout));
            if ((int?)reply["schemaVersion"] != SearchSchemaVersion)
                return Failed("its search reply is schema version " + reply["schemaVersion"] + ", not " + SearchSchemaVersion);

            List<PageHit> pages = (reply["results"] as JArray ?? new JArray())
                .Select(hit => new PageHit
                {
                    Path = (string)hit["path"],
                    Page = (int?)hit["page"] ?? 0,
                    Snippet = (string)hit["snippet"],
                    RecoveredByOcr = (bool?)hit["recoveredByOcr"] ?? false
                })
                .Where(hit => !string.IsNullOrEmpty(hit.Path) && hit.Page > 0 && !string.IsNullOrEmpty(hit.Snippet))
                .ToList();

            return pages.Count == 0
                ? new Result(Outcome.NothingMatched, null, "the index returned no pages")
                : new Result(Outcome.Found, null, null, pages);
        }

        private static Result Failed(string detail)
        {
            Diagnostics.Log.Warn("ManualForge's index could not be searched (" + detail + "); " +
                                 "choosing manuals by filename instead.");
            return new Result(Outcome.Failed, null, detail);
        }

        /// <summary>
        /// ManualForge does not set its console encoding, so with stdout redirected it writes in the OEM code
        /// page (measured: 'é' arrives as 0x82). Strict UTF-8 is tried first so a build that does set it keeps
        /// working; for an all-ASCII path, which is nearly every path, the two agree anyway.
        /// </summary>
        private static string Decode(byte[] bytes)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { /* not UTF-8 */ }

            try { return Encoding.GetEncoding(GetOEMCP()).GetString(bytes); }
            catch (Exception) { return Encoding.Default.GetString(bytes); }
        }

        [DllImport("kernel32.dll")]
        private static extern int GetOEMCP();

        /// <summary>
        /// Quotes one argument the way the C runtime will split it back out - a query is caller text, and a
        /// quote or a trailing backslash in it must arrive as itself rather than as the end of the argument.
        /// </summary>
        public static string QuoteArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument)) return "\"\"";
            if (argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return argument;

            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\') { backslashes++; continue; }

                // Backslashes are literal unless they precede a quote, where each must be doubled and the
                // quote itself escaped.
                quoted.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
                quoted.Append(c);
                backslashes = 0;
            }
            // Doubled before the closing quote, so a path ending in '\' does not escape it.
            quoted.Append('\\', backslashes * 2).Append('"');
            return quoted.ToString();
        }
    }
}
