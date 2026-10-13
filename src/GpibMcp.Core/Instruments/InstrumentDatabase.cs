using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GpibMcp.Diagnostics;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// In-memory collection of <see cref="InstrumentDefinition"/>s loaded from one or more
    /// directories of JSON files, indexed by model name and aliases (case-insensitive).
    ///
    /// Definitions from later directories override earlier ones with the same model, but the merge is
    /// <b>per top-level block</b>, not whole-file: a later file replaces only the top-level properties
    /// it defines and inherits the rest from the earlier file. So a user copy that predates a bundled
    /// improvement (e.g. lacks a <c>statusModel</c> block) still picks that block up from the bundled
    /// default, while the user's own blocks continue to win. This is how bundled fixes reach an
    /// already-seeded user database without clobbering user edits (issue #25). Whole-block value
    /// changes the user already overrides are not auto-merged - use <c>instrument_db_refresh</c>.
    ///
    /// Except where the bundled file says a block's earlier values are superseded: its top-level
    /// <c>supersedes</c> object lists, per block name, values it has shipped before. A user copy whose
    /// block is still exactly one of those - seeded from an older build, never edited - takes the bundled
    /// block instead, so a fix to that block reaches it (e.g. the 8560-family <c>statusModel</c>, #142).
    /// A block the user has changed matches none of them and keeps winning.
    /// </summary>
    public sealed class InstrumentDatabase
    {
        private readonly List<InstrumentDefinition> _all;
        private readonly Dictionary<string, InstrumentDefinition> _byKey =
            new Dictionary<string, InstrumentDefinition>(StringComparer.OrdinalIgnoreCase);

        private InstrumentDatabase(List<InstrumentDefinition> definitions)
        {
            _all = definitions;
            foreach (var d in definitions) Index(d);
        }

        public static InstrumentDatabase Empty() => new InstrumentDatabase(new List<InstrumentDefinition>());

        public static InstrumentDatabase FromDefinitions(IEnumerable<InstrumentDefinition> definitions) =>
            new InstrumentDatabase(definitions.ToList());

        /// <summary>
        /// Loads every *.json definition from the given directories. For the same model, later
        /// directories override earlier ones <b>per top-level block</b> (see the type remarks): a user
        /// file's blocks win, and any block it omits is inherited from the bundled default.
        /// </summary>
        public static InstrumentDatabase Load(IEnumerable<string> directories)
        {
            var order = new List<string>();                  // model insertion order (first seen)
            var byModel = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);

            foreach (var dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    var jo = TryLoadJson(file);
                    string model = jo == null ? null : (string)jo.GetValue("model", StringComparison.OrdinalIgnoreCase);
                    if (string.IsNullOrWhiteSpace(model)) continue;

                    JObject existing;
                    if (byModel.TryGetValue(model, out existing))
                        MergeTopLevel(existing, jo);         // overlay this dir's blocks onto the base
                    else { byModel[model] = jo; order.Add(model); }
                }
            }

            var ordered = new List<InstrumentDefinition>();
            foreach (var model in order)
            {
                var def = TryDeserialize(byModel[model]);
                if (def != null && !string.IsNullOrWhiteSpace(def.Model)) ordered.Add(def);
            }
            Log.Info("Instrument database: loaded " + ordered.Count + " definition(s)");
            return new InstrumentDatabase(ordered);
        }

        /// <summary>
        /// Overlays <paramref name="overlay"/>'s top-level properties onto <paramref name="baseObj"/>
        /// (each replaces or adds), leaving base-only blocks intact. Coarse by design: a block the
        /// overlay defines wins wholesale (no field-level merge of changed values, which would produce
        /// an inconsistent mix); a block it omits falls through from the base. A block that is still one
        /// of the base's superseded values (its <c>supersedes</c> list) does not replace the base's, and
        /// the base's <c>supersedes</c> list itself is kept.
        /// </summary>
        internal static void MergeTopLevel(JObject baseObj, JObject overlay)
        {
            var superseded = baseObj.GetValue(SupersedesKey, StringComparison.OrdinalIgnoreCase) as JObject;
            foreach (var prop in overlay.Properties())
            {
                if (string.Equals(prop.Name, SupersedesKey, StringComparison.OrdinalIgnoreCase) && superseded != null)
                    continue;
                if (IsSuperseded(superseded, prop))
                {
                    Log.Info("Instrument database: " + (string)baseObj.GetValue("model", StringComparison.OrdinalIgnoreCase) +
                             " - the user copy's '" + prop.Name + "' is an earlier bundled version, never edited; " +
                             "using the current bundled one.");
                    continue;
                }
                baseObj[prop.Name] = prop.Value.DeepClone();
            }
        }

        /// <summary>Top-level key listing, per block name, the block values a bundled definition has superseded.</summary>
        internal const string SupersedesKey = "supersedes";

        private static bool IsSuperseded(JObject superseded, JProperty block)
        {
            var earlier = superseded?.GetValue(block.Name, StringComparison.OrdinalIgnoreCase) as JArray;
            return earlier != null && earlier.Any(value => JToken.DeepEquals(value, block.Value));
        }

        public IReadOnlyList<InstrumentDefinition> All => _all;

        /// <summary>Looks up a definition by model name or alias.</summary>
        public bool TryGet(string modelOrAlias, out InstrumentDefinition definition)
        {
            definition = null;
            return !string.IsNullOrWhiteSpace(modelOrAlias) &&
                   _byKey.TryGetValue(modelOrAlias.Trim(), out definition);
        }

        /// <summary>Definitions whose identity pattern matches an identification response.</summary>
        public IEnumerable<InstrumentDefinition> MatchIdentity(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) yield break;
            foreach (var d in _all)
            {
                string pattern = d.Identity != null ? d.Identity.MatchRegex : null;
                if (string.IsNullOrWhiteSpace(pattern)) continue;
                bool matched;
                try { matched = Regex.IsMatch(response, pattern, RegexOptions.IgnoreCase); }
                catch (Exception) { matched = false; }
                if (matched) yield return d;
            }
        }

        /// <summary>Adds or replaces a definition at runtime (used after instrument_db_save).</summary>
        public void Upsert(InstrumentDefinition definition)
        {
            if (definition == null || string.IsNullOrWhiteSpace(definition.Model)) return;
            _all.RemoveAll(d => ModelEquals(d.Model, definition.Model));
            _all.Add(definition);
            Index(definition);
        }

        private void Index(InstrumentDefinition d)
        {
            if (!string.IsNullOrWhiteSpace(d.Model)) _byKey[d.Model.Trim()] = d;
            if (d.Aliases == null) return;
            foreach (var alias in d.Aliases)
                if (!string.IsNullOrWhiteSpace(alias)) _byKey[alias.Trim()] = d;
        }

        private static bool ModelEquals(string a, string b) =>
            string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static JObject TryLoadJson(string file)
        {
            try
            {
                return JObject.Parse(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load instrument definition '" + file + "': " + ex.Message);
                return null;
            }
        }

        private static InstrumentDefinition TryDeserialize(JObject jo)
        {
            try
            {
                return jo.ToObject<InstrumentDefinition>();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to parse instrument definition '" +
                         (string)jo.GetValue("model", StringComparison.OrdinalIgnoreCase) + "': " + ex.Message);
                return null;
            }
        }
    }
}
