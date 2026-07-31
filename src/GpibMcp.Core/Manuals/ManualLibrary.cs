using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GpibMcp.Manuals
{
    /// <summary>
    /// A user's folder of instrument manuals (#120), and the rules for finding which files in it are worth
    /// reading for a given question.
    ///
    /// Narrowing by filename first is not an optimisation detail, it is the design. A real library is
    /// hundreds of large PDFs - extracting all of them to answer one question would take minutes and produce
    /// mostly noise. Manuals are almost always named by model ("8560E Programming Guide.pdf", or a folder
    /// "3458A" holding its set), so the model name is a strong, cheap filter that gets to the right handful
    /// before any file is opened.
    ///
    /// The feature is off unless a folder is configured: no folder, no tool.
    /// </summary>
    public sealed class ManualLibrary
    {
        /// <summary>Extensions worth reading. Text needs no extraction; PDF needs <see cref="ManualText"/>.</summary>
        private static readonly string[] ReadableExtensions = { ".pdf", ".txt", ".md", ".text" };

        /// <summary>
        /// Ceiling on files opened for one search when the caller gave no model to narrow by. Bounded so a
        /// vague question cannot walk a 5 GB library; the search reports when it hits this, because a
        /// silently truncated search reads as "not in the manuals" when it means "we stopped looking".
        /// </summary>
        public const int MaxFilesPerSearch = 12;

        private readonly string _root;

        private ManualLibrary(string root) { _root = root; }

        /// <summary>The configured folder, or null when the feature is off.</summary>
        public string Root => _root;

        /// <summary>
        /// Opens the library named by <c>GPIB_MCP_MANUALS</c>, or returns null when it is unset or points
        /// nowhere. A configured-but-missing folder is worth a warning rather than silence - it is almost
        /// always a typo, and the alternative is a tool that quietly never finds anything.
        /// </summary>
        public static ManualLibrary FromEnvironment()
        {
            string configured = Environment.GetEnvironmentVariable("GPIB_MCP_MANUALS");
            if (string.IsNullOrWhiteSpace(configured)) return null;

            string root = configured.Trim().Trim('"');
            if (!Directory.Exists(root))
            {
                Diagnostics.Log.Warn("GPIB_MCP_MANUALS points at '" + root + "', which does not exist; " +
                                     "manual lookup is disabled.");
                return null;
            }
            return new ManualLibrary(root);
        }

        /// <summary>Opens a library at an explicit path (tests, and callers that configure it directly).</summary>
        public static ManualLibrary At(string root) =>
            string.IsNullOrWhiteSpace(root) || !Directory.Exists(root) ? null : new ManualLibrary(root.Trim());

        /// <summary>
        /// The files worth reading for this question, best candidates first. When a model is given, files
        /// whose name or folder mentions it come first and nothing else is opened unless there are none;
        /// otherwise the query's own words are matched against filenames.
        /// </summary>
        public IReadOnlyList<ManualFile> Candidates(string model, string query, int limit = MaxFilesPerSearch)
        {
            bool haveModel = !string.IsNullOrWhiteSpace(model);

            return Enumerate()
                .Select(f => new { File = f, Score = NameScore(f, model, query) })
                // With a model in hand, a file must be related to THAT instrument. Otherwise one shared word
                // in a filename - "frequency" appears in half a library - drags in application notes for
                // other instruments, which cost seconds to extract and answer a different question.
                .Where(x => x.Score > 0 && (!haveModel || ModelScore(x.File, model) > 0))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.File.SizeBytes)      // a short manual is likelier to be the relevant one
                .Select(x => x.File)
                .Take(Math.Max(1, limit))
                .ToList();

            // Deliberately no fallback to "read some files anyway". Reading arbitrary manuals to answer a
            // question about an instrument they do not cover costs seconds and returns noise - and worse,
            // it reports "searched 12 files, no match", which reads as "your library does not have this"
            // when the truth is "I never looked at the right file". Better to say nothing matched and show
            // what is there.
        }

        /// <summary>A sample of what the library holds, so a caller told "nothing matched" can retry usefully.</summary>
        public IReadOnlyList<string> SampleNames(string model, int limit = 20)
        {
            IEnumerable<ManualFile> files = Enumerate();

            // With a model in hand, prefer names that at least share its leading digits - "no 8563E manual,
            // but here are the 856x ones" is a far better prompt than an alphabetical slice of the library.
            string stem = FamilyStem(model);
            if (stem != null)
            {
                var near = files.Where(f => f.RelativePath.ToLowerInvariant().Contains(stem)).ToList();
                if (near.Count > 0) files = near;
            }

            return files.Select(f => f.RelativePath).OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                        .Take(Math.Max(1, limit)).ToList();
        }

        /// <summary>Total readable files, for telling the caller the size of what was not searched.</summary>
        public int Count() => Enumerate().Count();

        /// <summary>Every readable file in the library.</summary>
        public IEnumerable<ManualFile> Enumerate()
        {
            IEnumerable<string> paths;
            try { paths = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories); }
            catch (Exception ex)
            {
                Diagnostics.Log.Warn("Could not read the manual library at '" + _root + "': " + ex.Message);
                yield break;
            }

            foreach (string path in paths)
            {
                string ext = Path.GetExtension(path);
                if (Array.IndexOf(ReadableExtensions, ext.ToLowerInvariant()) < 0) continue;

                ManualFile file = null;
                try { file = new ManualFile(path, _root); }
                catch (Exception) { /* vanished or unreadable between enumerate and stat */ }

                // A zero-byte file is a failed download, not a manual.
                if (file != null && file.SizeBytes > 0) yield return file;
            }
        }

        /// <summary>
        /// Resolves a caller-supplied path against the library, refusing anything outside it. The path comes
        /// from a tool argument, so "../../../secrets.txt" has to bounce off something.
        /// </summary>
        public ManualFile Resolve(string relativeOrFullPath)
        {
            if (string.IsNullOrWhiteSpace(relativeOrFullPath)) return null;

            string candidate = relativeOrFullPath.Trim().Trim('"');
            string full;
            try
            {
                full = Path.GetFullPath(Path.IsPathRooted(candidate)
                    ? candidate
                    : Path.Combine(_root, candidate));
            }
            catch (Exception) { return null; }

            string rootFull = Path.GetFullPath(_root).TrimEnd(Path.DirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
            if (!full.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
            if (!File.Exists(full)) return null;

            return new ManualFile(full, _root);
        }

        /// <summary>
        /// How well a file's name matches the question. The model is worth far more than a query word: a
        /// filename containing "8563E" is almost certainly that instrument's manual, whereas one containing
        /// "frequency" is barely evidence at all.
        /// </summary>
        private static int NameScore(ManualFile file, string model, string query)
        {
            string haystack = file.RelativePath.Replace('\\', ' ').Replace('/', ' ').ToLowerInvariant();
            int score = ModelScore(file, model);

            foreach (string word in Words(query))
                if (word.Length >= 3 && haystack.Contains(word)) score += 5;

            return score;
        }

        /// <summary>
        /// How strongly a file's name ties it to <paramref name="model"/>: named for it, named for it without
        /// a trailing option letter, or merely of its family - in descending order of confidence, and zero
        /// for no relation at all.
        /// </summary>
        private static int ModelScore(ManualFile file, string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return 0;

            string haystack = file.RelativePath.Replace('\\', ' ').Replace('/', ' ').ToLowerInvariant();
            string m = model.Trim().ToLowerInvariant();

            if (haystack.Contains(m)) return 100;

            // Models are often written with a trailing option letter the file omits (54622D -> 54622).
            if (m.Length > 3 && haystack.Contains(m.Substring(0, m.Length - 1))) return 60;

            // Family match, worth much less: an 8563E's programming manual is often filed as the series -
            // "8560E Programming Guide" - and a human looking for it would reach for that too. Low score so
            // a genuine model match always wins, and the result says the substitution happened.
            string stem = FamilyStem(m);
            return stem != null && haystack.Contains(stem) ? 25 : 0;
        }

        /// <summary>True when this file is named for the model itself, not merely its family.</summary>
        public static bool MatchesModel(ManualFile file, string model) =>
            file != null && ModelScore(file, model) >= 60;

        /// <summary>
        /// The leading digits that identify an instrument family - "8563E" and "8560E" share "856". Null when
        /// the model has no usable numeric stem, in which case there is no family to guess at.
        /// </summary>
        public static string FamilyStem(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return null;

            string digits = new string(model.Trim().TakeWhile(char.IsDigit).ToArray());
            return digits.Length >= 3 ? digits.Substring(0, 3) : null;
        }

        /// <summary>True when a candidate was found only by family resemblance, not by naming the model.</summary>
        public static bool IsFamilyMatchOnly(ManualFile file, string model)
        {
            if (file == null || string.IsNullOrWhiteSpace(model)) return false;

            string haystack = file.RelativePath.ToLowerInvariant();
            string m = model.Trim().ToLowerInvariant();
            if (haystack.Contains(m)) return false;
            if (m.Length > 3 && haystack.Contains(m.Substring(0, m.Length - 1))) return false;

            string stem = FamilyStem(m);
            return stem != null && haystack.Contains(stem);
        }

        /// <summary>Splits a query into lower-cased words worth matching.</summary>
        public static IEnumerable<string> Words(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) yield break;
            foreach (string raw in query.Split(new[] { ' ', '\t', '\r', '\n', ',', ';', ':', '(', ')', '"', '\'' },
                                               StringSplitOptions.RemoveEmptyEntries))
            {
                string word = raw.Trim().ToLowerInvariant();
                if (word.Length > 0) yield return word;
            }
        }
    }

    /// <summary>One file in the manual library.</summary>
    public sealed class ManualFile
    {
        public ManualFile(string fullPath, string root)
        {
            FullPath = fullPath;
            var info = new FileInfo(fullPath);
            SizeBytes = info.Length;
            ModifiedUtc = info.LastWriteTimeUtc;

            string rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) +
                              Path.DirectorySeparatorChar;
            RelativePath = fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(rootFull.Length)
                : Path.GetFileName(fullPath);
        }

        public string FullPath { get; }

        /// <summary>Path within the library - what a citation shows, and what the caller passes back.</summary>
        public string RelativePath { get; }

        public long SizeBytes { get; }
        public DateTime ModifiedUtc { get; }

        public bool IsPdf =>
            string.Equals(Path.GetExtension(FullPath), ".pdf", StringComparison.OrdinalIgnoreCase);
    }
}
