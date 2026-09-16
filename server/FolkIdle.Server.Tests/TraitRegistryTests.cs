using System.Linq;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE BIT TABLE IS A PERSISTED FORMAT. A character's traits are
    /// stored as positions in a long, so moving a trait to another bit silently
    /// swaps every live character's traits. This pins every position.
    /// </summary>
    public class TraitRegistryTests
    {
        [Fact]
        public void EveryTraitKeepsItsBit()
        {
            var expected = new (string Key, int Bit, TraitRarity Rarity)[]
            {
                ("stout_heart", 0, TraitRarity.Common),
                ("keen_edge", 1, TraitRarity.Common),
                ("quick_hands", 2, TraitRarity.Common),
                ("green_thumb", 3, TraitRarity.Common),
                ("iron_blood", 4, TraitRarity.Rare),
                ("hawk_eye", 5, TraitRarity.Rare),
                ("swift_blood", 6, TraitRarity.Rare),
                ("nimble", 7, TraitRarity.Rare),
                ("blood_of_kings", 8, TraitRarity.Legendary),
                ("wolfs_hunger", 9, TraitRarity.Legendary),
                ("fae_touched", 10, TraitRarity.Legendary),
                ("thin_blood", 16, TraitRarity.Flaw),
                ("faint_heart", 17, TraitRarity.Flaw),
                ("clumsy_hands", 18, TraitRarity.Flaw),
            };

            Assert.Equal(expected.Length, TraitRegistry.All.Length);
            foreach (var (key, bit, rarity) in expected)
            {
                var def = TraitRegistry.All.Single(t => t.Key == key);
                Assert.Equal(bit, def.Bit);
                Assert.Equal(rarity, def.Rarity);
            }
        }

        [Fact]
        public void BitsAreUniqueAndFlawsLiveAboveSixteen()
        {
            Assert.Equal(TraitRegistry.All.Length, TraitRegistry.All.Select(t => t.Bit).Distinct().Count());
            Assert.All(TraitRegistry.All.Where(t => t.Rarity == TraitRarity.Flaw), t => Assert.True(t.Bit >= 16));
            Assert.All(TraitRegistry.All.Where(t => t.Rarity != TraitRarity.Flaw), t => Assert.True(t.Bit < 16));
            Assert.All(TraitRegistry.All, t => Assert.InRange(t.Bit, 0, 62));
        }

        [Fact]
        public void FlawsHurtAndEverythingElseHelps()
        {
            Assert.All(TraitRegistry.All, t =>
            {
                if (t.Rarity == TraitRarity.Flaw) Assert.True(t.Value < 0, t.Key);
                else Assert.True(t.Value > 0, t.Key);
                Assert.False(string.IsNullOrWhiteSpace(t.Name));
                Assert.False(string.IsNullOrWhiteSpace(t.Description));
            });
        }

        [Fact]
        public void MaskHelpersRoundTrip()
        {
            long mask = TraitRegistry.MaskOf(TraitRegistry.IronBlood, TraitRegistry.ThinBlood);
            Assert.True(TraitRegistry.Has(mask, TraitRegistry.IronBlood));
            Assert.False(TraitRegistry.Has(mask, TraitRegistry.HawkEye));
            Assert.Equal(2, TraitRegistry.CountOf(mask));
            Assert.Equal(new[] { TraitRegistry.IronBlood, TraitRegistry.ThinBlood }, TraitRegistry.BitsOf(mask).ToArray());
            Assert.True(TraitRegistry.IsFlaw(TraitRegistry.ThinBlood));
            Assert.False(TraitRegistry.IsFlaw(TraitRegistry.IronBlood));
            Assert.Equal(0L, TraitRegistry.KnownBitsMask & (1L << 11));
            Assert.Equal(4, TraitRegistry.OfRarity(TraitRarity.Rare).Count);
        }
    }
}
