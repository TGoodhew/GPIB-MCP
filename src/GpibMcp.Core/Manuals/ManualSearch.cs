using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

            /// <summary>True when every word of the query is in the passage, not only some of them.</summary>
            public bool HasEveryWord { get; set; }

            /// <summary>True for a snippet from ManualForge's index rather than a passage read from the file.</summary>
            public bool FromIndex { get; set; }

            /// <summary>True when the index holds this page's text only because OCR recovered it.</summary>
            public bool RecoveredByOcr { get; set; }
        }

        public sealed class FileNote
        {
            public string File { get; set; }
            public string Problem { get; set; }
        }

        public sealed class Results
        {
            public List<Hit> Hits { get; } = new List<Hit>();

            /// <summary>
            /// Files that could not be read, or only in part, and why - never silently dropped. A file read in
            /// part is in <see cref="Searched"/> too.
            /// </summary>
            public List<FileNote> Unreadable { get; } = new List<FileNote>();

            public List<string> Searched { get; } = new List<string>();

            /// <summary>True when the file list was cut to the cap - the caller must be told.</summary>
            public bool Truncated { get; set; }
        }

        /// <summary>
        /// How long a search may spend opening manuals. A manual is read once and kept, but a dozen read for the
        /// first time can take a minute, and Claude Desktop gives up on a call at about 30 s.
        /// </summary>
        public const int DefaultTimeBudgetMs = 15000;

        /// <summary>How a manual's text is read: <see cref="ManualText.Read"/>, or a stand-in for a test.</summary>
        internal static Func<ManualFile, ManualText.Result> ReadText = ManualText.Read;

        /// <summary>Manuals being read now, by path and state, so a search that comes back finds the same read.</summary>
        private static readonly Dictionary<string, Task<ManualText.Result>> Reading =
            new Dictionary<string, Task<ManualText.Result>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Reads <paramref name="file"/> on a thread of its own, or joins the read already under way. A search
        /// waits only for the time it has left, and a read it gave up on carries on and fills the cache, so a
        /// large manual is ready for the next search instead of being started again and abandoned each time.
        /// </summary>
        private static Task<ManualText.Result> StartReading(ManualFile file)
        {
            string key = file.FullPath + "|" + file.SizeBytes + "|" + file.ModifiedUtc.Ticks;
            lock (Reading)
            {
                Task<ManualText.Result> task;
                if (Reading.TryGetValue(key, out task)) return task;

                Func<ManualFile, ManualText.Result> read = ReadText;
                task = Task.Run(() =>
                {
                    try { return read(file); }
                    catch (Exception ex)
                    {
                        return new ManualText.Result(ManualText.Outcome.ExtractionFailed, null, "extraction failed: " + ex.Message);
                    }
                });
                Reading[key] = task;
                // Runs inline if the read has already finished; the lock is this thread's, so the entry goes at once.
                task.ContinueWith(_ => { lock (Reading) Reading.Remove(key); }, TaskContinuationOptions.ExecuteSynchronously);
                return task;
            }
        }

        /// <summary>
        /// Searches <paramref name="files"/> for <paramref name="query"/>, best passages first. Files not reached
        /// or not finished inside <paramref name="timeBudgetMs"/>, and pages that couldn't be read, are listed as
        /// unreadable: a search that stopped must never read as one that looked.
        /// </summary>
        /// <param name="onFile">Progress callback: (index, total, file being read).</param>
        public static Results Run(IReadOnlyList<ManualFile> files, string query, int maxHits = 5,
                                  int contextChars = DefaultContextChars, Action<int, int, string> onFile = null,
                                  int timeBudgetMs = DefaultTimeBudgetMs)
        {
            var results = new Results();
            if (files == null || files.Count == 0) return results;

            string[] words = ManualLibrary.Words(query).Where(w => w.Length >= 2).Distinct().ToArray();
            if (words.Length == 0) return results;

            var all = new List<Hit>();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < files.Count; i++)
            {
                ManualFile file = files[i];
                if (clock.ElapsedMilliseconds >= timeBudgetMs)
                {
                    results.Unreadable.Add(new FileNote
                    {
                        File = file.RelativePath,
                        Problem = "not reached before this search's time ran out. Search again: the manuals already " +
                                  "read are kept, so the next one gets further."
                    });
                    continue;
                }
                if (onFile != null) onFile(i, files.Count, file.RelativePath);

                Task<ManualText.Result> reading = StartReading(file);
                long left = timeBudgetMs - clock.ElapsedMilliseconds;
                if (!reading.Wait((int)Math.Max(0, Math.Min(left, int.MaxValue))))
                {
                    results.Unreadable.Add(new FileNote
                    {
                        File = file.RelativePath,
                        Problem = "still being read when this search's time ran out: a long manual takes a while the " +
                                  "first time. Reading carries on, so search again in a minute and it will be ready."
                    });
                    continue;
                }

                ManualText.Result text = reading.Result;
                if (!text.Ok)
                {
                    results.Unreadable.Add(new FileNote { File = file.RelativePath, Problem = text.Detail });
                    continue;
                }

                int pageCount;
                List<int> damaged = ManualText.UnreadablePages(text.Text, out pageCount);
                if (damaged.Count > 0 && damaged.Count == pageCount)
                {
                    results.Unreadable.Add(new FileNote
                    {
                        File = file.RelativePath,
                        Problem = "none of its " + pageCount + " page(s) could be read: the PDF looks damaged."
                    });
                    continue;
                }
                if (damaged.Count > 0)
                    results.Unreadable.Add(new FileNote
                    {
                        File = file.RelativePath,
                        Problem = PageList(damaged) + " could not be read, so " +
                                  (damaged.Count == 1 ? "it wasn't" : "they weren't") + " searched. The rest of the manual was."
                    });

                results.Searched.Add(file.RelativePath);
                all.AddRange(FindIn(file, text.Text, words, contextChars));
            }

            results.Hits.AddRange(all
                .OrderByDescending(h => h.Score)
                .Take(Math.Max(1, maxHits)));
            return results;
        }

        /// <summary>"page 4", "pages 4, 9 and 12", or the first ten and how many more.</summary>
        private static string PageList(List<int> pages)
        {
            if (pages.Count == 1) return "page " + pages[0];
            const int Shown = 10;
            List<string> shown = pages.Take(Shown).Select(p => p.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();
            string last = pages.Count > Shown ? (pages.Count - Shown) + " more" : shown[shown.Count - 1];
            if (pages.Count <= Shown) shown.RemoveAt(shown.Count - 1);
            return "pages " + string.Join(", ", shown) + " and " + last;
        }

        /// <summary>
        /// Adds <paramref name="pages"/> from the index, which match every word of the query, ahead of passages
        /// that match only some. A page a passage from the file already answered in full is skipped, since the
        /// passage says more than a snippet. One the file matched only in part gives way to the snippet: what it
        /// lacked is likely text only the index holds, recovered by OCR.
        /// </summary>
        public static void Fill(Results results, IEnumerable<Hit> pages, int maxHits)
        {
            var have = new HashSet<string>(results.Hits.Where(h => h.HasEveryWord).Select(Key), StringComparer.OrdinalIgnoreCase);
            List<Hit> indexed = pages.Where(p => have.Add(Key(p))).ToList();
            List<Hit> merged = results.Hits.Where(h => h.HasEveryWord)
                                      .Concat(indexed)
                                      .Concat(results.Hits.Where(h => !h.HasEveryWord && !have.Contains(Key(h))))
                                      .Take(Math.Max(1, maxHits))
                                      .ToList();
            results.Hits.Clear();
            results.Hits.AddRange(merged);
        }

        private static string Key(Hit hit) => hit.File + "|" + hit.Page;

        /// <summary>
        /// Scores each occurrence of the rarest query word by how many of the other words appear nearby.
        /// Anchoring on the rarest word is what makes a search for "center frequency CF" land on the page
        /// defining CF rather than the hundreds of pages that merely say "frequency".
        /// </summary>
        private static IEnumerable<Hit> FindIn(ManualFile file, string text, string[] words, int contextChars)
        {
            string lower = text.ToLowerInvariant();

            // The rarest word this manual has: one it doesn't, like a guess at an option's name, says nothing
            // about the words it does have.
            string anchor = words.Where(w => CountOccurrences(lower, w) > 0)
                                 .OrderBy(w => CountOccurrences(lower, w)).FirstOrDefault();
            if (anchor == null) yield break;
            int anchorCount = CountOccurrences(lower, anchor);

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

                int score = 10, found = 1;
                foreach (string word in words)
                    if (word != anchor && snippetLower.Contains(word)) { score += 20; found++; }

                // One hit per page: consecutive matches on a page are the same passage to a reader.
                int page = ManualText.PageAt(text, at);
                if (!seenPages.Add(page)) continue;

                yield return new Hit
                {
                    File = file.RelativePath,
                    Page = page,
                    Score = score,
                    Text = Tidy(snippet),
                    HasEveryWord = found == words.Length
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
