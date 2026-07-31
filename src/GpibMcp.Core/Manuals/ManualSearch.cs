using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GpibMcp.Manuals
{
    /// <summary>
    /// Finds the passages in a manual that answer a question (#120), and cites where each came from.
    ///
    /// This deliberately returns <b>passages, not answers</b>. Deriving "the command is CF" from a page of
    /// prose is the model's job, done in front of the user who can see the quoted text; a server that
    /// synthesised commands out of manuals would be guessing with far more confidence than the evidence
    /// supports, and the thing on the other end of a wrong guess is real hardware.
    /// </summary>
    public static class ManualSearch
    {
        /// <summary>Characters of context returned either side of a hit.</summary>
        public const int DefaultContextChars = 400;

        public sealed class Hit
        {
            public string File { get; set; }
            public int Page { get; set; }
            public int Score { get; set; }
            public string Text { get; set; }
        }

        public sealed class FileNote
        {
            public string File { get; set; }
            public string Problem { get; set; }
        }

        public sealed class Results
        {
            public List<Hit> Hits { get; } = new List<Hit>();

            /// <summary>Files that could not be read, and why - never silently dropped.</summary>
            public List<FileNote> Unreadable { get; } = new List<FileNote>();

            public List<string> Searched { get; } = new List<string>();

            /// <summary>True when the file list was cut to the cap - the caller must be told.</summary>
            public bool Truncated { get; set; }
        }

        /// <summary>
        /// Searches <paramref name="files"/> for <paramref name="query"/>, best passages first.
        /// </summary>
        /// <param name="onFile">Progress callback: (index, total, file being read).</param>
        public static Results Run(IReadOnlyList<ManualFile> files, string query, int maxHits = 5,
                                  int contextChars = DefaultContextChars, Action<int, int, string> onFile = null)
        {
            var results = new Results();
            if (files == null || files.Count == 0) return results;

            string[] words = ManualLibrary.Words(query).Where(w => w.Length >= 2).Distinct().ToArray();
            if (words.Length == 0) return results;

            var all = new List<Hit>();
            for (int i = 0; i < files.Count; i++)
            {
                ManualFile file = files[i];
                if (onFile != null) onFile(i, files.Count, file.RelativePath);

                ManualText.Result text = ManualText.Read(file);
                if (!text.Ok)
                {
                    results.Unreadable.Add(new FileNote { File = file.RelativePath, Problem = text.Detail });
                    continue;
                }

                results.Searched.Add(file.RelativePath);
                all.AddRange(FindIn(file, text.Text, words, contextChars));
            }

            results.Hits.AddRange(all
                .OrderByDescending(h => h.Score)
                .Take(Math.Max(1, maxHits)));
            return results;
        }

        /// <summary>
        /// Scores each occurrence of the rarest query word by how many of the other words appear nearby.
        /// Anchoring on the rarest word is what makes a search for "center frequency CF" land on the page
        /// defining CF rather than the hundreds of pages that merely say "frequency".
        /// </summary>
        private static IEnumerable<Hit> FindIn(ManualFile file, string text, string[] words, int contextChars)
        {
            string lower = text.ToLowerInvariant();

            string anchor = words.OrderBy(w => CountOccurrences(lower, w)).First();
            int anchorCount = CountOccurrences(lower, anchor);
            if (anchorCount == 0) yield break;

            // A word that appears everywhere is not evidence of anything; stop rather than return noise.
            const int TooCommon = 400;
            if (anchorCount > TooCommon) yield break;

            int window = Math.Max(contextChars, 200);
            int from = 0;
            var seenPages = new HashSet<int>();

            while (true)
            {
                int at = lower.IndexOf(anchor, from, StringComparison.Ordinal);
                if (at < 0) yield break;
                from = at + anchor.Length;

                int start = Math.Max(0, at - window / 2);
                int length = Math.Min(window, text.Length - start);
                string snippet = text.Substring(start, length);
                string snippetLower = snippet.ToLowerInvariant();

                int score = 10;
                foreach (string word in words)
                    if (word != anchor && snippetLower.Contains(word)) score += 20;

                // One hit per page: consecutive matches on a page are the same passage to a reader.
                int page = ManualText.PageAt(text, at);
                if (!seenPages.Add(page)) continue;

                yield return new Hit
                {
                    File = file.RelativePath,
                    Page = page,
                    Score = score,
                    Text = Tidy(snippet)
                };
            }
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0, at = 0;
            while ((at = haystack.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
            {
                count++;
                at += needle.Length;
            }
            return count;
        }

        /// <summary>
        /// Collapses the whitespace that <c>-layout</c> extraction leaves behind, while keeping line breaks:
        /// a command table read as one run-on line is useless, and read with 40 spaces between columns is
        /// mostly padding.
        /// </summary>
        private static string Tidy(string snippet)
        {
            var sb = new StringBuilder(snippet.Length);
            int spaces = 0;

            foreach (char c in snippet)
            {
                if (c == ManualText.PageBreak) { sb.Append('\n'); spaces = 0; continue; }
                if (c == '\n' || c == '\r') { if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n'); spaces = 0; continue; }
                if (c == ' ' || c == '\t')
                {
                    if (++spaces <= 2) sb.Append(' ');
                    continue;
                }
                spaces = 0;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }
    }
}
