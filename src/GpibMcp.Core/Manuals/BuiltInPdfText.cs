using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace GpibMcp.Manuals
{
    /// <summary>
    /// Reads a PDF's text layer with nothing else installed: what makes manual lookup work out of the box.
    /// Pages are separated by <see cref="ManualText.PageBreak"/> like pdftotext's, and each is laid out from the
    /// words' positions, as pdftotext -layout does, because a command table without its columns is unreadable.
    /// </summary>
    internal static class BuiltInPdfText
    {
        public static string Read(string path, int limitMs)
        {
            var clock = Stopwatch.StartNew();
            var text = new StringBuilder();
            using (PdfDocument document = PdfDocument.Open(path))
            {
                for (int number = 1; number <= document.NumberOfPages; number++)
                {
                    // It runs in this process, so unlike a converter it can't be killed: it has to stop itself.
                    if (clock.ElapsedMilliseconds > limitMs)
                        throw new TimeoutException("the PDF reader did not finish within " + limitMs / 1000 + "s");

                    if (number > 1) text.Append(ManualText.PageBreak);
                    var page = new StringBuilder();
                    try
                    {
                        AppendPage(page, document.GetPage(number));
                        text.Append(page);
                    }
                    catch (Exception ex) when (!(ex is OutOfMemoryException))
                    {
                        // One damaged page shouldn't lose a manual. Marking it keeps the numbering, and lets the
                        // search say it wasn't read rather than that it didn't match.
                        Diagnostics.Log.Debug("Page " + number + " of '" + path + "' unreadable: " + ex.Message);
                        text.Append(ManualText.UnreadablePage);
                    }
                }
            }
            return text.ToString();
        }

        private static void AppendPage(StringBuilder text, Page page)
        {
            List<Word> words = page.GetWords().Where(w => !string.IsNullOrWhiteSpace(w.Text)).ToList();
            if (words.Count == 0) return;

            double charWidth = Math.Max(1, Median(words.Select(w => w.BoundingBox.Width / w.Text.Length)));
            double sameLine = Math.Max(1, Median(words.Select(w => w.BoundingBox.Height)) / 2);
            double margin = words.Min(w => w.BoundingBox.Left);

            double baseline = double.NaN;
            var line = new List<Word>();
            foreach (Word word in words.OrderByDescending(w => w.BoundingBox.Bottom))
            {
                if (line.Count > 0 && Math.Abs(word.BoundingBox.Bottom - baseline) > sameLine)
                {
                    AppendLine(text, line, margin, charWidth);
                    line.Clear();
                }
                if (line.Count == 0) baseline = word.BoundingBox.Bottom;
                line.Add(word);
            }
            AppendLine(text, line, margin, charWidth);
        }

        private static void AppendLine(StringBuilder text, List<Word> line, double margin, double charWidth)
        {
            var row = new StringBuilder();
            foreach (Word word in line.OrderBy(w => w.BoundingBox.Left))
            {
                int column = (int)Math.Round((word.BoundingBox.Left - margin) / charWidth);
                row.Append(' ', row.Length == 0 ? column : Math.Max(1, column - row.Length));
                row.Append(word.Text);
            }
            text.Append(row).Append('\n');
        }

        private static double Median(IEnumerable<double> values)
        {
            List<double> sorted = values.OrderBy(v => v).ToList();
            return sorted[sorted.Count / 2];
        }
    }
}
