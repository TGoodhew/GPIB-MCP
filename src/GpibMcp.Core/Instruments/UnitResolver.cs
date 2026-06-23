using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace GpibMcp.Instruments
{
    /// <summary>Outcome of resolving a human value+unit to a parameter's wire token (issue #46).</summary>
    public sealed class UnitResolution
    {
        public bool Ok { get; }
        public double Value { get; }
        public string Token { get; }
        public string Error { get; }

        private UnitResolution(bool ok, double value, string token, string error)
        {
            Ok = ok; Value = value; Token = token; Error = error;
        }

        // Token is coerced to "" (never null) so a tokenless resolution is safe to splice into set templates.
        public static UnitResolution Resolved(double value, string token) => new UnitResolution(true, value, token ?? "", null);
        public static UnitResolution Fail(string error) => new UnitResolution(false, 0, null, error);

        /// <summary>The value+token as it goes on the wire, e.g. "1000 MZ" - or just the number for a
        /// tokenless (bare-number) parameter.</summary>
        public string Formatted => !Ok ? null
            : string.IsNullOrEmpty(Token) ? UnitResolver.FormatNumber(Value)
            : UnitResolver.FormatNumber(Value) + " " + Token;
    }

    /// <summary>
    /// Deterministically maps a human value+unit (e.g. <c>1 GHz</c>) to the exact wire token a parameter
    /// accepts (e.g. <c>1000 MZ</c>), converting within a physical quantity when the instrument has no
    /// token for the spoken unit. Non-linear/standalone units (dBm, dB, Ω) only match exactly - they are
    /// never numerically converted. The instrument data supplies token-&gt;unit; this supplies unit-&gt;scale.
    /// </summary>
    public static class UnitResolver
    {
        private enum Quantity
        {
            Frequency, Time, Voltage, Current, Resistance, Power, Ratio, Angle, Percent,
            VoltagePP, VoltageRMS, VoltagePeak, CurrentPP, PowerW, TimePerDiv, VoltPerDiv, SampleRate, BitRate,
            Other
        }

        private struct UnitInfo
        {
            public Quantity Quantity;
            public double Scale;     // multiplier to the quantity's base unit (Hz, s, V, A, Ω)
            public bool Linear;      // false for log/standalone units (dBm, dB) - exact match only
            public UnitInfo(Quantity q, double scale, bool linear) { Quantity = q; Scale = scale; Linear = linear; }
        }

        // Keyed by normalized unit name (see Normalize). Tokens carry these unit names in the DB (#46).
        private static readonly Dictionary<string, UnitInfo> Units = new Dictionary<string, UnitInfo>(StringComparer.Ordinal)
        {
            ["hz"]  = new UnitInfo(Quantity.Frequency, 1,    true),
            ["khz"] = new UnitInfo(Quantity.Frequency, 1e3,  true),
            ["mhz"] = new UnitInfo(Quantity.Frequency, 1e6,  true),
            ["ghz"] = new UnitInfo(Quantity.Frequency, 1e9,  true),
            ["thz"] = new UnitInfo(Quantity.Frequency, 1e12, true),

            ["s"]  = new UnitInfo(Quantity.Time, 1,     true),
            ["ms"] = new UnitInfo(Quantity.Time, 1e-3,  true),
            ["us"] = new UnitInfo(Quantity.Time, 1e-6,  true),
            ["ns"] = new UnitInfo(Quantity.Time, 1e-9,  true),
            ["ps"] = new UnitInfo(Quantity.Time, 1e-12, true),

            ["v"]  = new UnitInfo(Quantity.Voltage, 1,    true),
            ["kv"] = new UnitInfo(Quantity.Voltage, 1e3,  true),
            ["mv"] = new UnitInfo(Quantity.Voltage, 1e-3, true),
            ["uv"] = new UnitInfo(Quantity.Voltage, 1e-6, true),
            ["nv"] = new UnitInfo(Quantity.Voltage, 1e-9, true),

            ["a"]  = new UnitInfo(Quantity.Current, 1,    true),
            ["ma"] = new UnitInfo(Quantity.Current, 1e-3, true),
            ["ua"] = new UnitInfo(Quantity.Current, 1e-6, true),
            ["na"] = new UnitInfo(Quantity.Current, 1e-9, true),

            ["ohm"]  = new UnitInfo(Quantity.Resistance, 1,   true),
            ["kohm"] = new UnitInfo(Quantity.Resistance, 1e3, true),
            ["mohm"] = new UnitInfo(Quantity.Resistance, 1e6, true),

            ["dbm"]  = new UnitInfo(Quantity.Power, 1, false),
            ["dbuv"] = new UnitInfo(Quantity.Power, 1, false),
            ["db"]   = new UnitInfo(Quantity.Ratio, 1, false),

            ["deg"]   = new UnitInfo(Quantity.Angle, 1, true),
            ["rad"]   = new UnitInfo(Quantity.Angle, 180.0 / Math.PI, true),   // radians convert to/from degrees
            ["pirad"] = new UnitInfo(Quantity.Angle, 180.0, true),            // multiples of pi radians (HP ESG): 1 PIRAD = 180 deg
            ["%"]     = new UnitInfo(Quantity.Percent, 1, true),

            // Peak-to-peak / RMS / peak are distinct MEASURES of voltage (waveform-shape dependent), so they
            // convert only within their own family - never to/from plain V (#46 vocab extension).
            ["vpp"]  = new UnitInfo(Quantity.VoltagePP, 1,    true),
            ["mvpp"] = new UnitInfo(Quantity.VoltagePP, 1e-3, true),
            ["uvpp"] = new UnitInfo(Quantity.VoltagePP, 1e-6, true),
            ["kvpp"] = new UnitInfo(Quantity.VoltagePP, 1e3,  true),
            ["vrms"]  = new UnitInfo(Quantity.VoltageRMS, 1,    true),
            ["mvrms"] = new UnitInfo(Quantity.VoltageRMS, 1e-3, true),
            ["uvrms"] = new UnitInfo(Quantity.VoltageRMS, 1e-6, true),
            ["vpeak"]  = new UnitInfo(Quantity.VoltagePeak, 1,    true),
            ["mvpeak"] = new UnitInfo(Quantity.VoltagePeak, 1e-3, true),
            ["app"]  = new UnitInfo(Quantity.CurrentPP, 1,    true),   // amps peak-to-peak
            ["mapp"] = new UnitInfo(Quantity.CurrentPP, 1e-3, true),

            // Linear power (watts) - distinct from dBm/dBuV (log).
            ["w"]  = new UnitInfo(Quantity.PowerW, 1,    true),
            ["mw"] = new UnitInfo(Quantity.PowerW, 1e-3, true),
            ["uw"] = new UnitInfo(Quantity.PowerW, 1e-6, true),
            ["nw"] = new UnitInfo(Quantity.PowerW, 1e-9, true),
            ["kw"] = new UnitInfo(Quantity.PowerW, 1e3,  true),

            // Log level / ratio variants - exact match only, never converted.
            ["dbw"]  = new UnitInfo(Quantity.Power, 1, false),
            ["dbmv"] = new UnitInfo(Quantity.Power, 1, false),
            ["dbv"]  = new UnitInfo(Quantity.Power, 1, false),
            ["dbf"]  = new UnitInfo(Quantity.Power, 1, false),   // dB relative to 1 fW (FM receiver sensitivity)
            ["dbc"]  = new UnitInfo(Quantity.Ratio, 1, false),

            // Per-division scope scale (timebase / vertical) - convert within the family.
            ["s/div"]  = new UnitInfo(Quantity.TimePerDiv, 1,     true),
            ["ms/div"] = new UnitInfo(Quantity.TimePerDiv, 1e-3,  true),
            ["us/div"] = new UnitInfo(Quantity.TimePerDiv, 1e-6,  true),
            ["ns/div"] = new UnitInfo(Quantity.TimePerDiv, 1e-9,  true),
            ["ps/div"] = new UnitInfo(Quantity.TimePerDiv, 1e-12, true),
            ["v/div"]  = new UnitInfo(Quantity.VoltPerDiv, 1,    true),
            ["mv/div"] = new UnitInfo(Quantity.VoltPerDiv, 1e-3, true),
            ["uv/div"] = new UnitInfo(Quantity.VoltPerDiv, 1e-6, true),

            // Sample / bit rate - convert within the family.
            ["sps"]  = new UnitInfo(Quantity.SampleRate, 1,   true),
            ["ksps"] = new UnitInfo(Quantity.SampleRate, 1e3, true),
            ["msps"] = new UnitInfo(Quantity.SampleRate, 1e6, true),
            ["gsps"] = new UnitInfo(Quantity.SampleRate, 1e9, true),
            ["bps"]  = new UnitInfo(Quantity.BitRate, 1,   true),
            ["kbps"] = new UnitInfo(Quantity.BitRate, 1e3, true),
            ["mbps"] = new UnitInfo(Quantity.BitRate, 1e6, true),

            // Standalone units that are matched exactly and never converted.
            ["db/div"] = new UnitInfo(Quantity.Other, 1, false),
            ["db/ghz"] = new UnitInfo(Quantity.Other, 1, false),
            ["baud"]   = new UnitInfo(Quantity.Other, 1, false),
            ["plc"]    = new UnitInfo(Quantity.Other, 1, false),   // DMM integration time (power-line cycles)
        };

        /// <summary>
        /// The single canonical spelling for each known unit. The DB's <c>unit</c> field must use these
        /// EXACT strings everywhere (the human unit is consistent across files; only the token is
        /// device-specific) - the consistency guard enforces it. Micro is ASCII "u" for latin-1 safety.
        /// </summary>
        private static readonly Dictionary<string, string> CanonicalName = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["hz"] = "Hz", ["khz"] = "kHz", ["mhz"] = "MHz", ["ghz"] = "GHz", ["thz"] = "THz",
            ["s"] = "s", ["ms"] = "ms", ["us"] = "us", ["ns"] = "ns", ["ps"] = "ps",
            ["v"] = "V", ["kv"] = "kV", ["mv"] = "mV", ["uv"] = "uV", ["nv"] = "nV",
            ["a"] = "A", ["ma"] = "mA", ["ua"] = "uA", ["na"] = "nA",
            ["ohm"] = "Ohm", ["kohm"] = "kOhm", ["mohm"] = "MOhm",
            ["dbm"] = "dBm", ["dbuv"] = "dBuV", ["db"] = "dB",
            ["deg"] = "deg", ["rad"] = "rad", ["pirad"] = "pirad", ["%"] = "%",
            ["vpp"] = "Vpp", ["mvpp"] = "mVpp", ["uvpp"] = "uVpp", ["kvpp"] = "kVpp",
            ["vrms"] = "Vrms", ["mvrms"] = "mVrms", ["uvrms"] = "uVrms",
            ["vpeak"] = "Vpeak", ["mvpeak"] = "mVpeak",
            ["app"] = "App", ["mapp"] = "mApp",
            ["w"] = "W", ["mw"] = "mW", ["uw"] = "uW", ["nw"] = "nW", ["kw"] = "kW",
            ["dbw"] = "dBW", ["dbmv"] = "dBmV", ["dbv"] = "dBV", ["dbf"] = "dBf", ["dbc"] = "dBc",
            ["s/div"] = "s/div", ["ms/div"] = "ms/div", ["us/div"] = "us/div", ["ns/div"] = "ns/div", ["ps/div"] = "ps/div",
            ["v/div"] = "V/div", ["mv/div"] = "mV/div", ["uv/div"] = "uV/div",
            ["sps"] = "sps", ["ksps"] = "ksps", ["msps"] = "Msps", ["gsps"] = "Gsps",
            ["bps"] = "bps", ["kbps"] = "kbps", ["mbps"] = "Mbps",
            ["db/div"] = "dB/div", ["db/ghz"] = "dB/GHz", ["baud"] = "baud", ["plc"] = "PLC",
        };

        /// <summary>The canonical unit vocabulary the DB's <c>unit</c> fields must draw from (#46).</summary>
        public static IReadOnlyCollection<string> CanonicalUnits { get; } =
            CanonicalName.Values.Distinct().ToList();

        /// <summary>The canonical spelling of a unit (e.g. "MHZ"/"mhz" -&gt; "MHz"), or null if unrecognised.</summary>
        public static string Canonical(string unit) =>
            CanonicalName.TryGetValue(Normalize(unit), out string c) ? c : null;

        /// <summary>True if the resolver knows this unit's quantity/scale (so a token using it is usable).</summary>
        public static bool IsKnownUnit(string unit) => Units.ContainsKey(Normalize(unit));

        /// <summary>
        /// Resolves <paramref name="value"/> <paramref name="fromUnit"/> to one of <paramref name="tokens"/>:
        /// exact unit match keeps the number; otherwise converts to the best same-quantity token. A null/empty
        /// unit is accepted only when there is a single audited token. Returns a failure with a clear reason.
        /// </summary>
        public static UnitResolution Resolve(double value, string fromUnit, IEnumerable<UnitToken> tokens)
        {
            var audited = (tokens ?? Enumerable.Empty<UnitToken>())
                .Where(t => t != null && t.IsAudited && Units.ContainsKey(Normalize(t.Unit)))
                .ToList();
            if (audited.Count == 0)
                return UnitResolution.Fail("this parameter has no audited unit tokens to set against (issue #46).");

            string from = Normalize(fromUnit);
            if (string.IsNullOrEmpty(from))
            {
                // No unit given: only safe if there is exactly one token to mean.
                if (audited.Count == 1) return UnitResolution.Resolved(value, audited[0].Token);
                return UnitResolution.Fail("a unit is required; this parameter accepts " + TokenList(audited) + ".");
            }
            if (!Units.TryGetValue(from, out UnitInfo fromInfo))
                return UnitResolution.Fail("unrecognised unit '" + fromUnit + "'.");

            // 1) Exact unit match - send the number unchanged.
            var exact = audited.FirstOrDefault(t => Normalize(t.Unit) == from);
            if (exact != null) return UnitResolution.Resolved(value, exact.Token);

            // 2) Same-quantity linear conversion. Non-linear units (dBm, dB) never convert.
            if (!fromInfo.Linear)
                return UnitResolution.Fail("'" + fromUnit + "' is not numerically convertible; this parameter accepts " + TokenList(audited) + ".");

            var candidates = audited
                .Where(t => { var i = Units[Normalize(t.Unit)]; return i.Linear && i.Quantity == fromInfo.Quantity; })
                .ToList();
            if (candidates.Count == 0)
                return UnitResolution.Fail("this parameter accepts " + TokenList(audited) + ", which can't represent '" + fromUnit + "'.");

            double baseValue = value * fromInfo.Scale;
            UnitToken best = PickToken(candidates, Math.Abs(baseValue));
            double resolved = baseValue / Units[Normalize(best.Unit)].Scale;
            return UnitResolution.Resolved(resolved, best.Token);
        }

        /// <summary>Picks the token giving the tidiest number: the largest unit whose result is &gt;= 1, else the smallest unit.</summary>
        private static UnitToken PickToken(List<UnitToken> candidates, double baseMagnitude)
        {
            UnitToken bestFit = null; double bestFitScale = -1;
            UnitToken smallest = null; double smallestScale = double.MaxValue;
            foreach (var t in candidates)
            {
                double scale = Units[Normalize(t.Unit)].Scale;
                if (scale <= baseMagnitude && scale > bestFitScale) { bestFitScale = scale; bestFit = t; }
                if (scale < smallestScale) { smallestScale = scale; smallest = t; }
            }
            return bestFit ?? smallest;
        }

        private static string TokenList(IEnumerable<UnitToken> tokens) =>
            string.Join(", ", tokens.Select(t => t.Token + " (" + t.Unit + ")"));

        /// <summary>Formats a resolved value without floating-point cruft (integer when whole).</summary>
        public static string FormatNumber(double value)
        {
            if (value == Math.Floor(value) && !double.IsInfinity(value) && Math.Abs(value) < 1e15)
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            return value.ToString("0.##########", CultureInfo.InvariantCulture);
        }

        /// <summary>Normalises a unit name for lookup: lower-case, no spaces, micro-sign to 'u', common synonyms folded.</summary>
        private static string Normalize(string unit)
        {
            if (string.IsNullOrWhiteSpace(unit)) return "";
            string u = unit.Trim().ToLowerInvariant().Replace("µ", "u").Replace("μ", "u").Replace(" ", "");
            switch (u)
            {
                case "sec": case "second": case "seconds": return "s";
                case "hertz": return "hz";
                case "ohms": case "Ω": return "ohm";
                case "kohms": return "kohm";
                case "degree": case "degrees": case "deg.": case "°": return "deg";
                case "radian": case "radians": return "rad";
                case "πrad": case "pi-rad": case "pirads": case "pi*rad": return "pirad";
                case "volt": case "volts": case "vdc": return "v";
                case "vpk": case "vp": return "vpeak";          // peak volts (distinct from V/Vpp/Vrms)
                case "vp-p": case "vpkpk": return "vpp";
                case "amp": case "amps": case "adc": return "a";
                case "watt": case "watts": return "w";
                case "sa/s": case "samples/s": case "sample/s": return "sps";
                case "bit/s": case "bits/s": return "bps";
                default: return u;
            }
        }
    }
}
