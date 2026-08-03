using FolkIdle.Server.Domain.Combat;
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
        public void TheRetiredHerbalismTrackStillRoutesAwayFromMining()
        {
            // The track survives on the payload and the wire even though no
            // node feeds it - deleting a field to remove content is how a
            // packet layout breaks. What matters is that it never lands in
            // Mining, which is the bug this suite exists for.
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

            // Modul: Herbalism retired - the design list has no herb in it and
            // no herbalism tool where axes, pickaxes and rods all exist in five
            // tiers. Its band stays RESERVED rather than reused, so a character
            // row still holding activity 4003 resolves to nothing instead of
            // quietly becoming a fishing node.
            Assert.False(ContentRegistry.TryGetGatheringNode(ActivityIdBands.RetiredHerbalismBand + 1, out _));
            Assert.False(ContentRegistry.TryGetGatheringNode(ActivityIdBands.RetiredHerbalismBand + 3, out _));
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
