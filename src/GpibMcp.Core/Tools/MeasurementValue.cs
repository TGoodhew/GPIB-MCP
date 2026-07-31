using System;
using System.Globalization;
using System.Linq;
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
        /// Matches a wire query string against a documented command: first on the documented query form
        /// ("CF?"), then on the mnemonic with the '?' taken off. Matching is exact-after-normalization on
        /// purpose - a fuzzy match here would attach a confident unit to the wrong reading.
        /// </summary>
        private static InstrumentCommand FindQueried(InstrumentDefinition def, string command)
        {
            if (def?.Commands == null || string.IsNullOrWhiteSpace(command)) return null;

            string wire = Normalize(command);
            if (wire.Length == 0) return null;
            string bare = wire.TrimEnd('?').Trim();

            return def.Commands.FirstOrDefault(c => Normalize(c.Query) == wire)
                ?? def.Commands.FirstOrDefault(c => bare.Length > 0 && Normalize(c.Mnemonic) == bare);
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
