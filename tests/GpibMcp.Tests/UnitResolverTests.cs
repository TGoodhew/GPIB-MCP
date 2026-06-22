using System.Collections.Generic;
using GpibMcp.Instruments;
using Xunit;

namespace GpibMcp.Tests
{
    public class UnitResolverTests
    {
        // A legacy HP frequency parameter: tokens HZ/KZ/MZ (no GHz token), mapped to canonical units.
        private static List<UnitToken> FreqHzKzMz() => new List<UnitToken>
        {
            new UnitToken("HZ", "Hz"), new UnitToken("KZ", "kHz"), new UnitToken("MZ", "MHz"),
        };

        [Fact]
        public void ExactUnitMatch_SendsNumberUnchanged()
        {
            var r = UnitResolver.Resolve(1, "MHz", FreqHzKzMz());
            Assert.True(r.Ok);
            Assert.Equal("1 MZ", r.Formatted);
        }

        [Fact]
        public void Converts_GHz_To_MZ_WhenNoGhzToken()
        {
            // The motivating case: 1 GHz on a box that only speaks up to MHz -> 1000 MZ.
            var r = UnitResolver.Resolve(1, "GHz", FreqHzKzMz());
            Assert.True(r.Ok);
            Assert.Equal("1000 MZ", r.Formatted);
        }

        [Fact]
        public void Uses_GZ_Directly_WhenAvailable()
        {
            var tokens = new List<UnitToken> { new UnitToken("MZ", "MHz"), new UnitToken("GZ", "GHz") };
            var r = UnitResolver.Resolve(1, "GHz", tokens);
            Assert.True(r.Ok);
            Assert.Equal("1 GZ", r.Formatted);
        }

        [Fact]
        public void PicksTidiestToken_ForFractionalConversions()
        {
            // GHz isn't a token, so it must convert; 0.0015 GHz = 1.5 MHz -> tidiest token MZ.
            var r = UnitResolver.Resolve(0.0015, "GHz", FreqHzKzMz());
            Assert.True(r.Ok);
            Assert.Equal("1.5 MZ", r.Formatted);
        }

        [Fact]
        public void ExactMatch_WinsOverConversion()
        {
            // The box has a kHz token and the user said kHz - send it as-is, don't "tidy" to MHz.
            var r = UnitResolver.Resolve(1500, "kHz", FreqHzKzMz());
            Assert.True(r.Ok);
            Assert.Equal("1500 KZ", r.Formatted);
        }

        [Fact]
        public void BelowSmallestToken_UsesSmallestToken()
        {
            var tokens = new List<UnitToken> { new UnitToken("KZ", "kHz"), new UnitToken("MZ", "MHz") };
            var r = UnitResolver.Resolve(500, "Hz", tokens); // 0.5 kHz (no Hz token)
            Assert.True(r.Ok);
            Assert.Equal("0.5 KZ", r.Formatted);
        }

        [Fact]
        public void NonLinearUnit_OnlyMatchesExactly()
        {
            var dbm = new List<UnitToken> { new UnitToken("DM", "dBm") };
            Assert.Equal("-10 DM", UnitResolver.Resolve(-10, "dBm", dbm).Formatted);

            // dBm is not numerically convertible to a voltage token.
            var mv = new List<UnitToken> { new UnitToken("MV", "mV") };
            Assert.False(UnitResolver.Resolve(-10, "dBm", mv).Ok);
        }

        [Fact]
        public void NoUnit_Ok_WhenSingleToken_Else_Fails()
        {
            var one = new List<UnitToken> { new UnitToken("DM", "dBm") };
            Assert.Equal("50 DM", UnitResolver.Resolve(50, null, one).Formatted);

            var r = UnitResolver.Resolve(1, "", FreqHzKzMz());
            Assert.False(r.Ok);
            Assert.Contains("unit is required", r.Error);
        }

        [Fact]
        public void UnauditedTokens_AreNotUsable()
        {
            var raw = new List<UnitToken> { new UnitToken("HZ"), new UnitToken("MZ") }; // Unit == null
            var r = UnitResolver.Resolve(1, "MHz", raw);
            Assert.False(r.Ok);
            Assert.Contains("no audited unit tokens", r.Error);
        }

        [Fact]
        public void UnknownUnit_Fails()
        {
            var r = UnitResolver.Resolve(1, "furlong", FreqHzKzMz());
            Assert.False(r.Ok);
            Assert.Contains("unrecognised unit", r.Error);
        }

        [Theory]
        [InlineData("MHZ", "MHz")]
        [InlineData("mhz", "MHz")]
        [InlineData(" GHz ", "GHz")]
        [InlineData("uV", "uV")]
        [InlineData("microvolt", null)]   // not a recognised spelling
        [InlineData("xyz", null)]
        public void Canonical_FoldsSpellings(string input, string expected)
        {
            Assert.Equal(expected, UnitResolver.Canonical(input));
        }

        [Fact]
        public void CanonicalUnits_AreSelfConsistent()
        {
            // Every canonical unit must canonicalise to itself (the audit/guard relies on this).
            foreach (var u in UnitResolver.CanonicalUnits)
                Assert.Equal(u, UnitResolver.Canonical(u));
        }
    }
}
