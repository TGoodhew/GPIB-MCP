using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using Newtonsoft.Json.Linq;
using Xunit;

namespace GpibMcp.Tests
{
    /// <summary>
    /// Consistency guards over the bundled instrument database (data/instruments/*.json) so the
    /// problems found in issue #41 cannot regress: every file parses, no two files claim the same
    /// model/alias (no clashing definitions), and every identity block is schema-conformant.
    /// </summary>
    public class InstrumentDbConsistencyTests
    {
        // Recognised IdentitySpec keys; anything else means a non-conforming block (e.g. the old
        // 5350A query/response/example shape, which the loader silently ignored).
        private static readonly HashSet<string> IdentityKeys =
            new HashSet<string>(StringComparer.Ordinal) { "command", "matchRegex", "supported", "description" };

        private static string DataDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "data", "instruments");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("Could not locate data/instruments from " + AppContext.BaseDirectory);
        }

        private static IEnumerable<(string File, JObject Json)> AllDefinitions()
        {
            foreach (var f in Directory.GetFiles(DataDir(), "*.json"))
                yield return (Path.GetFileName(f), JObject.Parse(File.ReadAllText(f)));
        }

        [Fact]
        public void EveryFile_ParsesToAnInstrumentDefinitionWithAModel()
        {
            foreach (var (file, json) in AllDefinitions())
            {
                var def = json.ToObject<InstrumentDefinition>();
                Assert.True(def != null && !string.IsNullOrWhiteSpace(def.Model), file + " has no model");
            }
        }

        [Fact]
        public void AuditedUnitTokens_UseTheCanonicalUnitVocabulary()
        {
            // A token's `unit` (once audited) must be one canonical spelling everywhere, so the resolver can
            // map a human value to it (#46). Unaudited bare-string tokens (unit null) are skipped - they are
            // the migration backlog, flagged elsewhere. This guard keeps the audited entries consistent.
            var offenders = new List<string>();
            foreach (var (file, json) in AllDefinitions())
            {
                var def = json.ToObject<InstrumentDefinition>();
                foreach (var cmd in def.Commands ?? Enumerable.Empty<InstrumentCommand>())
                    foreach (var p in cmd.Parameters ?? Enumerable.Empty<CommandParameter>())
                        foreach (var t in p.Units ?? Enumerable.Empty<UnitToken>())
                        {
                            if (!t.IsAudited) continue;
                            string canon = UnitResolver.Canonical(t.Unit);
                            if (canon != t.Unit)
                                offenders.Add(file + ": " + cmd.Mnemonic + "/" + p.Name + " token '" + t.Token +
                                    "' unit '" + t.Unit + "' -> " + (canon == null ? "UNKNOWN unit" : "should be '" + canon + "'"));
                        }
            }
            Assert.True(offenders.Count == 0,
                "Audited unit tokens must use the canonical unit vocabulary (#46):\n  " + string.Join("\n  ", offenders));
        }

        [Fact]
        public void NoTwoFiles_ClaimTheSameModelOrAlias()
        {
            // Maps each lower-cased model/alias to the file(s) that claim it. Any name claimed by more
            // than one file is a clash (the 8656B-on-8657B bug, the 5352A double-claim, etc.).
            var claims = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (file, json) in AllDefinitions())
            {
                var names = new List<string> { (string)json["model"] };
                if (json["aliases"] is JArray aliases)
                    names.AddRange(aliases.Select(a => (string)a));
                foreach (var n in names.Where(n => !string.IsNullOrWhiteSpace(n)))
                {
                    if (!claims.TryGetValue(n, out var files)) claims[n] = files = new List<string>();
                    if (!files.Contains(file)) files.Add(file);
                }
            }

            var clashes = claims.Where(kv => kv.Value.Count > 1)
                                .Select(kv => kv.Key + " -> " + string.Join(", ", kv.Value))
                                .ToList();
            Assert.True(clashes.Count == 0, "Clashing model/alias claims:\n  " + string.Join("\n  ", clashes));
        }

        [Fact]
        public void EveryIdentityBlock_IsSchemaConformant()
        {
            var problems = new List<string>();
            foreach (var (file, json) in AllDefinitions())
            {
                if (!(json["identity"] is JObject identity)) continue; // null/absent identity is allowed

                var unknown = identity.Properties().Select(p => p.Name).Where(k => !IdentityKeys.Contains(k)).ToList();
                if (unknown.Count > 0)
                    problems.Add(file + ": unknown identity keys [" + string.Join(", ", unknown) + "]");

                bool unsupported = identity["supported"]?.Type == JTokenType.Boolean &&
                                   !identity["supported"].Value<bool>();
                bool hasCommand = !string.IsNullOrEmpty((string)identity["command"]);
                if (!unsupported && !hasCommand)
                    problems.Add(file + ": identity must set supported=false or provide a command");
            }
            Assert.True(problems.Count == 0, "Non-conformant identity blocks:\n  " + string.Join("\n  ", problems));
        }
    }
}
