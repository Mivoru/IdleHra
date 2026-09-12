using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE TROPHY, asked for in the same breath as the wall.
    ///
    /// "It would be cool if the first clear would drop one of the things
    /// (equipment) in transcendent tier, but only on first clear."
    ///
    /// Transcendent is QualityTier 14 - five affixes and the top of
    /// RarityTier.PowerMultiplier. It is also effectively unobtainable any other
    /// way: fusion cannot exceed the Forge's ceiling of 12, and the drop roll for
    /// tier 14 is a weight of 0.0001 against a total of 196.7, about one in two
    /// million. So these five trophies - one per region boss, once each, ever -
    /// are the game's only reliable source of its best item frame, which is
    /// exactly the right reward for the only fight that is a wall.
    /// </summary>
    public class BossFirstClearTrophyTests
    {
        private readonly ITestOutputHelper _output;

        public BossFirstClearTrophyTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        [Fact]
        public void EveryRegionBossHasATrophyToGive()
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);

                Assert.True(BossFirstClearTrophy.TryChooseItemId(bossId, roll: 0, out int itemId),
                    $"region {region}'s boss has no trophy to give, so its first clear would pay nothing.");

                _output.WriteLine($"region {region}: {ContentRegistry.GetItemBaseId(itemId)}");
            }
        }

        /// <summary>
        /// The trophy comes out of the boss's OWN drop table, which is what makes
        /// it region-correct without a second derivation of "what does region N
        /// drop" - and that table already excludes tools and the catalogue's two
        /// unequippable slot names.
        /// </summary>
        [Fact]
        public void TheTrophyIsSomethingThatBossCouldHaveDroppedAnyway()
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
                var drops = EquipmentDropTable.GetDrops(bossId);

                for (int roll = 0; roll < 20; roll++)
                {
                    Assert.True(BossFirstClearTrophy.TryChooseItemId(bossId, roll, out int itemId));
                    Assert.Contains(itemId, drops.ToArray());

                    // A combat slot, never a tool. There are ELEVEN equipment
                    // slots and 8 Axe / 9 Pickaxe / 10 Rod are tools; a
                    // Transcendent pickaxe is not a trophy for killing a boss.
                    string baseId = ContentRegistry.GetItemBaseId(itemId);
                    Assert.NotEqual(EquipmentSlotKind.Tool, AffixRegistry.ResolveSlot(baseId));
                    Assert.NotEqual(EquipmentSlotKind.Unknown, AffixRegistry.ResolveSlot(baseId));
                }
            }
        }

        /// <summary>
        /// The roll spreads over the table rather than always handing out the
        /// same piece - five identical chestplates would be a worse reward than
        /// five different ones, and a player who re-rolls a season should not get
        /// a scripted outcome.
        /// </summary>
        [Fact]
        public void TheTrophyVariesWithTheRoll()
        {
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(5);
            var seen = new System.Collections.Generic.HashSet<int>();

            for (int roll = 0; roll < 50; roll++)
            {
                Assert.True(BossFirstClearTrophy.TryChooseItemId(bossId, roll, out int itemId));
                seen.Add(itemId);
            }

            Assert.True(seen.Count > 1, "every roll produced the same trophy");
        }

        [Fact]
        public void ATrophyIsTranscendentAndCarriesFiveAffixes()
        {
            Assert.Equal(RarityTier.Transcendent, BossFirstClearTrophy.QualityTier);
            Assert.Equal(5, RarityTier.GetAffixCount(BossFirstClearTrophy.QualityTier));
        }

        /// <summary>
        /// An ordinary monster has no first clear and therefore no trophy. The
        /// region is asked of the unlock registry rather than derived from the id,
        /// for the same reason BossFirstClearRules asks it.
        /// </summary>
        [Fact]
        public void AnOrdinaryMonsterHasNoTrophy()
        {
            int firstRegular = RaceUnlockRegistry.GetRegionBossMonsterId(1) - 1;

            Assert.False(BossFirstClearTrophy.TryChooseItemId(firstRegular, roll: 0, out _));
        }
    }
}
