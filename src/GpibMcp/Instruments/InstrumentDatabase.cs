using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using GpibMcp.Diagnostics;
using Newtonsoft.Json;

namespace GpibMcp.Instruments
{
    /// <summary>
    /// In-memory collection of <see cref="InstrumentDefinition"/>s loaded from one or more
    /// directories of JSON files, indexed by model name and aliases (case-insensitive).
    /// Definitions from later directories override earlier ones with the same model, so a
    /// user directory can override the bundled defaults.
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

        /// <summary>Loads every *.json definition from the given directories (later wins on conflict).</summary>
        public static InstrumentDatabase Load(IEnumerable<string> directories)
        {
            var ordered = new List<InstrumentDefinition>();
            foreach (var dir in directories)
            {
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    var def = TryLoadFile(file);
                    if (def == null || string.IsNullOrWhiteSpace(def.Model)) continue;
                    ordered.RemoveAll(d => ModelEquals(d.Model, def.Model));
                    ordered.Add(def);
                }
            }
            Log.Info("Instrument database: loaded " + ordered.Count + " definition(s)");
            return new InstrumentDatabase(ordered);
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

        private static InstrumentDefinition TryLoadFile(string file)
        {
            try
            {
                return JsonConvert.DeserializeObject<InstrumentDefinition>(File.ReadAllText(file));
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load instrument definition '" + file + "': " + ex.Message);
                return null;
            }
        }
    }
}
