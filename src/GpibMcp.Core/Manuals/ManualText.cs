using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Manuals
{
    /// <summary>
    /// Turns a manual into searchable text (#120) - the part that decides what this feature can do at all.
    ///
    /// .NET Framework cannot read a PDF on its own, and bundling a PDF engine into a server whose whole shape
    /// is "no external dependencies" would be a poor trade for a feature that is off by default. So there are
    /// three routes, in order:
    ///
    /// <list type="number">
    /// <item>the file is already text (.txt/.md) - read it;</item>
    /// <item>a sidecar <c>&lt;name&gt;.txt</c> sits beside the PDF - read that (how a user without the tool
    ///       below can still use this feature: extract once, however they like, and leave the text there);</item>
    /// <item><c>pdftotext</c> is on PATH (Poppler, xpdf) - run it with <c>-layout</c>, which keeps the column
    ///       alignment that command tables in instrument manuals depend on.</item>
    /// </list>
    ///
    /// If none applies the answer is an explicit "this file could not be read, here is how to fix it", never
    /// silence - a manual that cannot be extracted looking identical to a manual with no match would make the
    /// whole tool untrustworthy.
    ///
    /// Extraction is cached under <c>%LOCALAPPDATA%\GpibMcp\manual-text</c>, keyed by path, size and
    /// modification time, because it is slow and the same manual is read over and over.
    /// </summary>
    public static class ManualText
    {
        /// <summary>Page separator emitted by pdftotext; how a citation gets a page number.</summary>
        public const char PageBreak = '\f';

        /// <summary>Extraction is bounded: a runaway converter must not hang a tool call.</summary>
        private const int ExtractTimeoutMs = 60000;

        /// <summary>Why a file has no text, when it has none.</summary>
        public enum Outcome
        {
            Extracted,
            NoExtractorAvailable,
            ExtractionFailed
        }

        public sealed class Result
        {
            public Result(Outcome outcome, string text, string detail)
            {
                Outcome = outcome;
                Text = text ?? string.Empty;
                Detail = detail;
            }

            public Outcome Outcome { get; }
            public string Text { get; }

            /// <summary>Human-readable reason, when there is no text.</summary>
            public string Detail { get; }

            public bool Ok => Outcome == Outcome.Extracted;
        }

        /// <summary>Reads <paramref name="file"/> as text, using the cache when it is still valid.</summary>
        public static Result Read(ManualFile file)
        {
            if (file == null) return new Result(Outcome.ExtractionFailed, null, "no file");

            if (!file.IsPdf)
            {
                try { return new Result(Outcome.Extracted, File.ReadAllText(file.FullPath), null); }
                catch (Exception ex)
                {
                    return new Result(Outcome.ExtractionFailed, null, "could not read the file: " + ex.Message);
                }
            }

            string cached = ReadCache(file);
            if (cached != null) return new Result(Outcome.Extracted, cached, null);

            // A sidecar means someone already did the extraction; trust it over re-running a converter.
            string sidecar = Path.ChangeExtension(file.FullPath, ".txt");
            if (File.Exists(sidecar))
            {
                try
                {
                    string text = File.ReadAllText(sidecar);
                    WriteCache(file, text);
                    return new Result(Outcome.Extracted, text, null);
                }
                catch (Exception ex)
                {
                    Diagnostics.Log.Debug("Sidecar '" + sidecar + "' unreadable: " + ex.Message);
                }
            }

            string converter = FindPdfToText();
            if (converter == null)
                return new Result(Outcome.NoExtractorAvailable, null,
                    "this is a PDF and no text extractor is available. Install Poppler or xpdf so that " +
                    "'pdftotext' is on PATH (or set GPIB_MCP_PDFTOTEXT to its full path), or place an " +
                    "extracted '" + Path.GetFileNameWithoutExtension(file.FullPath) + ".txt' next to it.");

            try
            {
                string text = RunPdfToText(converter, file.FullPath);
                if (string.IsNullOrWhiteSpace(text))
                    return new Result(Outcome.ExtractionFailed, null,
                        "the extractor produced no text - the manual is most likely a scan, which would need OCR.");

                WriteCache(file, text);
                return new Result(Outcome.Extracted, text, null);
            }
            catch (Exception ex)
            {
                return new Result(Outcome.ExtractionFailed, null, "extraction failed: " + ex.Message);
            }
        }

        /// <summary>The 1-based page a character offset falls on, by counting page breaks before it.</summary>
        public static int PageAt(string text, int offset)
        {
            if (string.IsNullOrEmpty(text) || offset <= 0) return 1;

            int page = 1;
            int end = Math.Min(offset, text.Length);
            for (int i = 0; i < end; i++) if (text[i] == PageBreak) page++;
            return page;
        }

        /// <summary>The extractor to use, or null when there is none.</summary>
        public static string FindPdfToText()
        {
            string configured = Environment.GetEnvironmentVariable("GPIB_MCP_PDFTOTEXT");
            if (!string.IsNullOrWhiteSpace(configured))
            {
                string path = configured.Trim().Trim('"');
                return File.Exists(path) ? path : null;
            }

            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim().Trim('"'), "pdftotext.exe"); }
                catch (Exception) { continue; }   // a malformed PATH entry
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        private static string RunPdfToText(string converter, string pdfPath)
        {
            string output = Path.Combine(Path.GetTempPath(), "gpibmcp-manual-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                // -layout preserves column alignment, which is what makes a command table readable.
                var psi = new ProcessStartInfo(converter, "-layout -q \"" + pdfPath + "\" \"" + output + "\"")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) throw new Exception("could not start '" + converter + "'");
                    string stderr = process.StandardError.ReadToEnd();
                    if (!process.WaitForExit(ExtractTimeoutMs))
                    {
                        try { process.Kill(); } catch { /* already gone */ }
                        throw new Exception("the extractor did not finish within " + (ExtractTimeoutMs / 1000) + "s");
                    }
                    if (process.ExitCode != 0)
                        throw new Exception("the extractor failed (exit " + process.ExitCode + ")" +
                                            (string.IsNullOrWhiteSpace(stderr) ? "" : ": " + stderr.Trim()));
                }

                return File.Exists(output) ? File.ReadAllText(output) : string.Empty;
            }
            finally
            {
                try { if (File.Exists(output)) File.Delete(output); } catch { /* best effort */ }
            }
        }

        // ---- cache ----------------------------------------------------------

        /// <summary>Where extracted text is kept, so a manual is converted once rather than per search.</summary>
        public static string CacheDirectory()
        {
            string env = Environment.GetEnvironmentVariable("GPIB_MCP_MANUAL_CACHE");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            return Path.Combine(InstrumentPaths.AppDataDir(), "manual-text");
        }

        private static string CachePath(ManualFile file)
        {
            // Keyed by identity AND state: a re-scanned or replaced manual must not serve stale text.
            string key = file.FullPath.ToLowerInvariant() + "|" + file.SizeBytes + "|" +
                         file.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture);

            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var name = new StringBuilder(32);
                for (int i = 0; i < 16; i++) name.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
                return Path.Combine(CacheDirectory(), name + ".txt");
            }
        }

        private static string ReadCache(ManualFile file)
        {
            try
            {
                string path = CachePath(file);
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception) { return null; }
        }

        private static void WriteCache(ManualFile file, string text)
        {
            try
            {
                string path = CachePath(file);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text);
            }
            catch (Exception ex)
            {
                // A cache that cannot be written is slow, not broken.
                Diagnostics.Log.Debug("Could not cache extracted text: " + ex.Message);
            }
        }
    }
}
