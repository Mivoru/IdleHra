using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Which profession a gather levels, pinned.
    ///
    /// Every XP router in the engine read
    /// `professionType == 0 ? Woodcutting : Mining` - the ternary was written
    /// twice, once in the bulk warp path and once in the realtime tick. There
    /// were only two mastery tracks on the wire, so Fishing (2) and Herbalism
    /// (3) both landed in MINING. Fishing a node in band 3000 raised the
    /// player's mining level, and Fishing could not be displayed at all
    /// because no field carried it.
    ///
    /// Reported from a live session: "when I go fishing, XP is added to mining
    /// and fishing is not there at all".
    /// </summary>
    public class GatheringMasteryRoutingTests
    {
        private const int Woodcutting = 0;
        private const int Mining = 1;
        private const int Fishing = 2;
        private const int Herbalism = 3;

        // Modul: the content has to be LOADED to be asserted on. Without this
        // the registry was empty, and the retired-herbalism check ("no node in
        // 4000") passed only because no node existed anywhere.
        public GatheringMasteryRoutingTests() => ContentRegistry.Initialize();

        // 50 * (0 + 1)^2 = 50 xp is exactly one level from zero.
        private const int OneLevel = 50;

        [Theory]
        [InlineData(Woodcutting)]
        [InlineData(Mining)]
        [InlineData(Fishing)]
        public void EachProfessionLevelsOnlyItself(int professionType)
        {
            var payload = new TickStatePayload();
            SimulationEngine.ApplyBulkMasteryXp(ref payload, professionType, OneLevel);

            for (int other = Woodcutting; other <= Herbalism; other++)
            {
                int level = SimulationEngine.GetMasteryLevel(ref payload, other);
                if (other == professionType)
                {
                    Assert.Equal(1, level);
                }
                else
                {
                    Assert.True(level == 0, $"profession {professionType} leaked a level into {other}");
                }
            }
        }

        [Fact]
        public void FishingDoesNotTouchMining()
        {
            // The exact reported symptom, stated on its own so a regression
            // names itself rather than being one row of a theory.
            var payload = new TickStatePayload();
            SimulationEngine.ApplyBulkMasteryXp(ref payload, Fishing, 10_000);

            Assert.Equal(0, payload.MiningMasteryLevel);
            Assert.Equal(0, payload.MiningMasteryXp);
            Assert.True(payload.FishingMasteryLevel > 0, "fishing should have levelled");
        }

        [Fact]
        public void TheHerbalismTrackRoutesAwayFromMining()
        {
            // The track survived on the payload and the wire while herbalism
            // was retired, and its nodes feed it again since 2026-10-30. What
            // matters is that it never lands in Mining, which is the bug this
            // suite exists for.
            var payload = new TickStatePayload();
            SimulationEngine.ApplyBulkMasteryXp(ref payload, Herbalism, 10_000);

            Assert.Equal(0, payload.MiningMasteryLevel);
            Assert.True(payload.HerbalismMasteryLevel > 0);
        }

        [Fact]
        public void TheBandForEachProfessionMatchesItsMasteryTrack()
        {
            // The band and the track are authored in two different files. If
            // they ever disagree, a node's XP goes to a profession the player
            // did not choose - which is the bug this suite exists for.
            Assert.Equal(ActivityIdBands.WoodcuttingBand, ActivityIdBands.GetBandForProfession(Woodcutting));
            Assert.Equal(ActivityIdBands.MiningBand, ActivityIdBands.GetBandForProfession(Mining));
            Assert.Equal(ActivityIdBands.FishingBand, ActivityIdBands.GetBandForProfession(Fishing));
            Assert.Equal(ActivityIdBands.HerbalismBand, ActivityIdBands.GetBandForProfession(Herbalism));

            // Modul: herbalism came back in its own band (2026-10-30). Every
            // herb node must be profession 3, so its XP lands on the herbalism
            // track and never on fishing's.
            for (int location = 1; location <= ContentRegistry.LocationCount; location++)
            {
                Assert.True(ContentRegistry.TryGetGatheringNode(ActivityIdBands.HerbalismBand + location, out var node));
                Assert.Equal(Herbalism, node.ProfessionType);

                // The 10-weight herb is the one R1 calls rare, the common is not,
                // and neither is edible (the larder reads the fishing band only).
                var table = ContentRegistry.GetLootTable(node.ActivityId);
                Assert.False(Domain.Progression.VillageManagementEngine.IsRareMaterial(ContentRegistry.GetItemBaseId(table[0].ItemId)));
                Assert.True(Domain.Progression.VillageManagementEngine.IsRareMaterial(ContentRegistry.GetItemBaseId(table[1].ItemId)));
                Assert.Equal(location, ContentRegistry.ItemDefinitions[table[1].ItemId - 1].RegionTier);
                Assert.False(ContentRegistry.IsRawFish(table[0].ItemId));
                Assert.False(ContentRegistry.IsRawFish(table[1].ItemId));
            }
        }

        [Fact]
        public void HerbsArePickedByHand_NoToolSpeedsThem()
        {
            // Owner, 2026-10-30: herbalism has no tool, the mastery is the
            // speed. Before it came back the tool switch fell through to the
            // rod, so a fisher's rod would have sped up a herbalist.
            var payload = new TickStatePayload { RodToolTier = 10, AxeToolTier = 10, PickaxeToolTier = 10, HerbalismMasteryLevel = 25 };
            var terms = SimulationEngine.GatherSpeedTermsFor(ref payload, Herbalism);
            Assert.Equal(0, terms.ToolTier);
            Assert.Equal(0, SimulationEngine.ToolSpeedPctFor(in payload, Herbalism));
            Assert.Equal(GatheringToolEngine.GetMasterySpeedBonusPct(25), terms.TotalPct);
        }

        [Fact]
        public void MasteryXpAccumulatesWithoutOverflowingIntoANegativeRequirement()
        {
            // The requirement curve is 50*(level+1)^2, which passes int.MaxValue
            // past level ~6500. Computed as int it wraps negative and the
            // while loop then levels forever.
            var payload = new TickStatePayload();
            SimulationEngine.ApplyBulkMasteryXp(ref payload, Fishing, int.MaxValue);

            Assert.True(payload.FishingMasteryLevel > 0);
            Assert.True(payload.FishingMasteryXp >= 0, "xp must never go negative");
        }
    }
}
