// -----------------------------------------------------------------------------
// Hpgl.Rendering - HP-GL/2 vector-to-bitmap renderer (.NET Framework 4.7.2).
//
// The HP-GL plotter-emulation capture-and-render technique that motivates this
// library is derived from the HP7470A Plotter Emulator (7470.cpp) by John Miles,
// KE5FX. Original C++ author: John Miles (KE5FX) - http://www.ke5fx.com/
// This independent C# adaptation carries no warranty from KE5FX.
// -----------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Hpgl.Rendering
{
    /// <summary>One parsed HP-GL instruction: a two-letter mnemonic plus parameters.</summary>
    internal sealed class HpglInstruction
    {
        public string Mnemonic { get; }
        public IReadOnlyList<double> Parameters { get; }

        /// <summary>Raw text payload for the LB (label) instruction; otherwise null.</summary>
        public string Text { get; }

        public HpglInstruction(string mnemonic, IReadOnlyList<double> parameters, string text = null)
        {
            Mnemonic = mnemonic;
            Parameters = parameters ?? Array.Empty<double>();
            Text = text;
        }
    }

    /// <summary>
    /// Tokenizes an HP-GL/2 stream into a flat list of <see cref="HpglInstruction"/>.
    /// Handles the special LB (label, read until terminator) and DT (define
    /// terminator) instructions; everything else is mnemonic + numeric parameters.
    /// </summary>
    internal static class HpglParser
    {
        private const char Etx = '\u0003'; // default LB terminator (ETX)

        public static List<HpglInstruction> Parse(string source)
        {
            var result = new List<HpglInstruction>();
            if (string.IsNullOrEmpty(source)) return result;

            char terminator = Etx;
            int i = 0, n = source.Length;

            while (i < n)
            {
                if (!char.IsLetter(source[i])) { i++; continue; }
                if (i + 1 >= n) break;

                char a = char.ToUpperInvariant(source[i]);
                char b = source[i + 1];
                if (!char.IsLetter(b)) { i++; continue; } // stray letter; skip
                string mnemonic = new string(new[] { a, char.ToUpperInvariant(b) });
                i += 2;

                if (mnemonic == "LB")
                {
                    var sb = new StringBuilder();
                    while (i < n && source[i] != terminator) sb.Append(source[i++]);
                    if (i < n) i++; // consume terminator
                    result.Add(new HpglInstruction("LB", null, sb.ToString()));
                    continue;
                }

                if (mnemonic == "DT")
                {
                    // DT <terminatorChar>[,mode];  - the char immediately follows DT.
                    if (i < n && source[i] != ';')
                    {
                        terminator = source[i++];
                        while (i < n && source[i] != ';') i++; // skip optional ,mode
                    }
                    else
                    {
                        terminator = Etx;
                    }
                    if (i < n && source[i] == ';') i++;
                    result.Add(new HpglInstruction("DT", null));
                    continue;
                }

                int start = i;
                while (i < n && source[i] != ';' && !char.IsLetter(source[i])) i++;
                string paramText = source.Substring(start, i - start);
                if (i < n && source[i] == ';') i++;
                result.Add(new HpglInstruction(mnemonic, ParseNumbers(paramText)));
            }

            return result;
        }

        private static List<double> ParseNumbers(string text)
        {
            var numbers = new List<double>();
            if (string.IsNullOrWhiteSpace(text)) return numbers;

            foreach (var token in text.Split(new[] { ',', ' ', '\t', '\r', '\n' },
                                             StringSplitOptions.RemoveEmptyEntries))
            {
                double value;
                if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    numbers.Add(value);
            }
            return numbers;
        }
    }
}
