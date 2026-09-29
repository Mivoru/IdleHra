using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Task 81: the chest's per-region auto-sell rules. They are folded into
    /// the one tier a drop request already carried, so the loot worker's
    /// salvage test is unchanged. These tests pin the fold and the packing.
    /// The worker side is pinned by DropRecordTests.AutoSalvagedDrops_*.
    /// </summary>
    public class ChestSalvageRulesTests
    {
        public ChestSalvageRulesTests()
        {
            ContentRegistry.Initialize();
        }

        [Fact]
        public void PackAndUnpack_RoundTripEveryRegion()
        {
            int[] tiers = { 1, 0, 6, 3, 2 };
            Assert.True(ChestSalvageRules.TryPack(tiers, out int packed));
            Assert.Equal(tiers, ChestSalvageRules.Unpack(packed));
            for (int region = 1; region <= ChestSalvageRules.RegionCount; region++)
            {
                Assert.Equal(tiers[region - 1], ChestSalvageRules.RegionTier(packed, region));
            }
        }

        [Theory]
        [InlineData(new[] { 0, 0, 0, 0 })]            // too few regions
        [InlineData(new[] { 0, 0, 0, 0, 0, 0 })]      // too many
        [InlineData(new[] { 0, 0, 7, 0, 0 })]         // above MaxSweepableQualityTier: Legendary+ is never auto-sold
        [InlineData(new[] { 0, -1, 0, 0, 0 })]
        public void TryPack_RefusesRatherThanClamps(int[] tiers)
        {
            Assert.False(ChestSalvageRules.TryPack(tiers, out _));
        }

        [Fact]
        public void Sanitise_ClampsAHandWrittenRowBelowLegendary()
        {
            // 15 in region 2's nibble - reachable only by writing the column by hand.
            int handWritten = 15 << 4;
            int clean = ChestSalvageRules.Sanitise(handWritten);
            Assert.Equal(VillageChestEngine.MaxSweepableQualityTier, ChestSalvageRules.RegionTier(clean, 2));
        }

        [Fact]
        public void ARegionRule_OnlyRaisesTheGlobalFloor()
        {
            Assert.True(ChestSalvageRules.TryPack(new[] { 3, 0, 0, 0, 1 }, out int packed));

            Assert.Equal(3, ChestSalvageRules.EffectiveTier(globalTier: 0, packed, region: 1));
            Assert.Equal(2, ChestSalvageRules.EffectiveTier(globalTier: 2, packed, region: 2)); // no rule: global
            Assert.Equal(2, ChestSalvageRules.EffectiveTier(globalTier: 2, packed, region: 5)); // lower rule: global wins
            Assert.Equal(0, ChestSalvageRules.EffectiveTier(globalTier: 0, packed, region: 3)); // nothing set: keep all
            Assert.Equal(2, ChestSalvageRules.EffectiveTier(globalTier: 2, packed, region: 9)); // outside the canon: global only
        }

        [Fact]
        public void TheDropRequest_CarriesTheTierForTheMonstersOwnRegion()
        {
            // Monster ids are canonical: 91-95 are region 1, 111-115 region 5.
            Assert.Equal(1, ContentRegistry.GetMonsterRegionTier(91));
            Assert.Equal(5, ContentRegistry.GetMonsterRegionTier(111));

            var payload = RarityRollDistributionTests.Level94Region5Payload();
            payload.AutoSalvageBelowTier = 1;
            Assert.True(ChestSalvageRules.TryPack(new[] { 4, 0, 0, 0, 0 }, out payload.AutoSalvageRegionTiers));
            var stats = RarityRollDistributionTests.StatsFor(in payload);

            var regionOne = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 91, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);
            var regionFive = CombatLootDropRequest.Build(
                in payload, in stats, monsterId: 111, kills: 1, bonusRarityTiers: 0, skipMaterialRoll: false);

            Assert.Equal(4, regionOne.AutoSalvageBelowTier);
            Assert.Equal(1, regionFive.AutoSalvageBelowTier);
        }

        [Fact]
        public void AMidSessionChange_ReachesTheLivePayload()
        {
            // Hydrated at login, so without the notification a new rule would
            // wait for the next sign-in - a toggle that seems to do nothing.
            var payload = new TickStatePayload { PlayerId = 7 };
            Assert.True(ChestSalvageRules.TryPack(new[] { 0, 2, 0, 0, 0 }, out int packed));

            VillageChestTickCoordinator.ApplyChestSettings(ref payload, new ChestSettingsNotification
            {
                PlayerId = 7,
                AutoSalvageBelowTier = 1,
                AutoSalvageRegionTiers = packed,
            });

            Assert.Equal(1, payload.AutoSalvageBelowTier);
            Assert.Equal(packed, payload.AutoSalvageRegionTiers);
            Assert.True(payload.IsDirty);
        }
    }
}
