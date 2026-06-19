using System.Collections.Generic;
using System.IO;
using System.Linq;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class InstrumentDatabaseTests
    {
        private static InstrumentDefinition Def(string model, params string[] aliases) =>
            new InstrumentDefinition
            {
                Model = model,
                Aliases = aliases.ToList(),
                Identity = new IdentitySpec { Command = "ID?", MatchRegex = model },
                Commands = new List<InstrumentCommand>
                {
                    new InstrumentCommand { Name = "frequency", Mnemonic = "FR" }
                }
            };

        [Fact]
        public void TryGet_ResolvesByModelAndAlias_CaseInsensitive()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Def("8563E", "8563EC", "HP8563E") });

            Assert.True(db.TryGet("8563E", out var byModel));
            Assert.Equal("8563E", byModel.Model);
            Assert.True(db.TryGet("hp8563e", out var byAlias));
            Assert.Equal("8563E", byAlias.Model);
            Assert.False(db.TryGet("9000Z", out _));
        }

        [Fact]
        public void MatchIdentity_ReturnsModelsWhosePatternMatches()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Def("8563E"), Def("3325A") });
            var matches = db.MatchIdentity("HP8563E/EC").Select(d => d.Model).ToList();
            Assert.Equal(new[] { "8563E" }, matches);
        }

        [Fact]
        public void Upsert_ReplacesExistingModel()
        {
            var db = InstrumentDatabase.FromDefinitions(new[] { Def("X") });
            db.Upsert(new InstrumentDefinition { Model = "X", Category = "Updated" });
            Assert.Single(db.All);
            Assert.True(db.TryGet("X", out var d));
            Assert.Equal("Updated", d.Category);
        }

        [Fact]
        public void Load_ReadsJsonFiles_AndUserDirOverridesBundled()
        {
            string bundled = NewTempDir();
            string user = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(bundled, "x.json"),
                    "{\"model\":\"X\",\"category\":\"bundled\"}");
                File.WriteAllText(Path.Combine(bundled, "y.json"),
                    "{\"model\":\"Y\",\"category\":\"bundled\"}");
                File.WriteAllText(Path.Combine(user, "x.json"),
                    "{\"model\":\"X\",\"category\":\"user\"}");

                var db = InstrumentDatabase.Load(new[] { bundled, user });

                Assert.Equal(2, db.All.Count);
                Assert.True(db.TryGet("X", out var x));
                Assert.Equal("user", x.Category); // user dir wins
            }
            finally
            {
                Directory.Delete(bundled, true);
                Directory.Delete(user, true);
            }
        }

        [Fact]
        public void Load_AcceptsScalarOrArrayForStringListFields()
        {
            string dir = NewTempDir();
            try
            {
                // units/aliases/examples written as scalars (a common hand-authoring mistake)
                // must still load, coerced to single-element lists.
                File.WriteAllText(Path.Combine(dir, "scalar.json"),
                    "{\"model\":\"S\",\"aliases\":\"S1\"," +
                    "\"commands\":[{\"name\":\"f\",\"examples\":\"FREQ 1\"," +
                    "\"parameters\":[{\"name\":\"freq\",\"units\":\"Hz\"}]}]}");

                var db = InstrumentDatabase.Load(new[] { dir });

                Assert.True(db.TryGet("S", out var s));
                Assert.True(db.TryGet("S1", out _)); // scalar alias coerced + indexed
                var p = s.Commands[0].Parameters[0];
                Assert.Equal(new[] { "Hz" }, p.Units);
                Assert.Equal(new[] { "FREQ 1" }, s.Commands[0].Examples.ToArray());
            }
            finally { Directory.Delete(dir, true); }
        }

        [Fact]
        public void Load_SkipsMalformedFiles()
        {
            string dir = NewTempDir();
            try
            {
                File.WriteAllText(Path.Combine(dir, "good.json"), "{\"model\":\"Good\"}");
                File.WriteAllText(Path.Combine(dir, "bad.json"), "{ not valid json");
                var db = InstrumentDatabase.Load(new[] { dir });
                Assert.Single(db.All);
                Assert.True(db.TryGet("Good", out _));
            }
            finally { Directory.Delete(dir, true); }
        }

        private static string NewTempDir()
        {
            // Date.Now/Guid are fine in product test code; use a counter-free unique temp name.
            string path = Path.Combine(Path.GetTempPath(), "gpibdb_test_" + Path.GetRandomFileName());
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
