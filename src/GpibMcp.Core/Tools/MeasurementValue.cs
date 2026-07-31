using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GpibMcp.Instruments;

namespace GpibMcp.Tools
{
    /// <summary>
    /// Turns an instrument's raw reply into the two things a caller actually wants from a measurement: the
    /// number, and what it is measured in (#113).
    ///
    /// The number comes from the reply. The unit cannot - a bare "1.5E+9" off the wire says nothing about
    /// hertz - so it comes from the command database, from exactly the audited unit tokens #46 established.
    /// That is the point of the tie-in: the audit recorded what each command's value means, and this is
    /// where that knowledge reaches the model without being stringified first. When the reply is not a plain
    /// number, or the command is not one we have audited units for, the answer is null rather than a guess.
    /// </summary>
    internal static class MeasurementValue
    {
        /// <summary>
        /// Parses a reply that is a single number ("-12.34", "1.5E+9", " +7 "). Returns false for anything
        /// else - a list, an identity string, a status word - rather than pulling a number out of prose.
        /// </summary>
        public static bool TryParseNumber(string response, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(response)) return false;
            return double.TryParse(response.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// The physical unit a query's reply is in, from the model's audited unit tokens, or null when the
        /// command is unknown to the database or its units were never audited.
        /// </summary>
        public static string UnitForQuery(InstrumentDefinition def, string command)
        {
            InstrumentCommand cmd = FindQueried(def, command);
            if (cmd?.Parameters == null) return null;

            return cmd.Parameters
                .Where(p => p.Units != null)
                .SelectMany(p => p.Units)
                .FirstOrDefault(u => u.IsAudited)?.Unit;
        }

        /// <summary>
        /// Matches a wire query string against a documented command - which is not string equality, because
        /// the database documents SCPI in the specification's own notation and the wire carries an
        /// abbreviation of it.
        ///
        /// <c>":FREQuency?"</c> means the instrument accepts both <c>:FREQ?</c> (the capitals: the short
        /// form) and <c>:FREQUENCY?</c> (the whole word), and square brackets mark optional nodes:
        /// <c>"[:SOURce]:FREQuency[:CW]"</c> is <c>:FREQ</c> as much as it is <c>:SOUR:FREQ:CW</c>. So a
        /// documented form expands to the handful of strings that mean it, and the wire command has to equal
        /// one of them. Still exact after expansion, on purpose: a fuzzy match would attach a confident unit
        /// to the wrong reading.
        /// </summary>
        private static InstrumentCommand FindQueried(InstrumentDefinition def, string command)
        {
            if (def?.Commands == null || string.IsNullOrWhiteSpace(command)) return null;

            string wire = Normalize(command);
            if (wire.Length == 0) return null;
            string bare = wire.TrimEnd('?');

            return def.Commands.FirstOrDefault(c => WireForms(c.Query).Contains(wire))
                ?? def.Commands.FirstOrDefault(c => bare.Length > 0 && WireForms(c.Mnemonic).Contains(bare));
        }

        /// <summary>
        /// Every string a documented SCPI form can arrive as: short and long spellings, with the optional
        /// nodes present and absent. Only the two extremes of optionality are generated - all optional nodes
        /// or none - which covers how instruments are actually addressed without enumerating every subset.
        /// </summary>
        private static HashSet<string> WireForms(string documented)
        {
            var forms = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(documented)) return forms;

            foreach (string variant in new[] { WithOptionalNodes(documented), WithoutOptionalNodes(documented) })
            {
                if (variant.Length == 0) continue;
                forms.Add(Normalize(ShortForm(variant)));
                forms.Add(Normalize(variant));
            }
            forms.Remove(string.Empty);
            return forms;
        }

        /// <summary>Keeps the bracketed nodes, dropping only the brackets: "[:SOURce]:FREQuency" -> ":SOURce:FREQuency".</summary>
        private static string WithOptionalNodes(string documented) =>
            new string(documented.Where(ch => ch != '[' && ch != ']').ToArray());

        /// <summary>Drops the bracketed nodes entirely: "[:SOURce]:FREQuency[:CW]" -> ":FREQuency".</summary>
        private static string WithoutOptionalNodes(string documented)
        {
            var sb = new StringBuilder(documented.Length);
            int depth = 0;

            foreach (char ch in documented)
            {
                if (ch == '[') { depth++; continue; }
                if (ch == ']') { if (depth > 0) depth--; continue; }
                if (depth == 0) sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>
        /// The SCPI short form: the capitals are the abbreviation, the lower-case tail is optional. Anything
        /// that is not a letter (colons, digits, '?', '*') is structure and always stays.
        /// </summary>
        private static string ShortForm(string documented)
        {
            var sb = new StringBuilder(documented.Length);
            foreach (char ch in documented)
                if (!char.IsLetter(ch) || char.IsUpper(ch)) sb.Append(ch);
            return sb.ToString();
        }

        /// <summary>Upper-cases and strips whitespace/terminators so "  cf? \n" and "CF?" are one thing.</summary>
        private static string Normalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return new string(s.Where(ch => !char.IsWhiteSpace(ch)).ToArray())
                .ToUpperInvariant();
        }
    }
}
