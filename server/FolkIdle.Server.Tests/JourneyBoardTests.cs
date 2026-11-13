using System.Linq;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>"Your road" (2026-11-13): the endgame tracks, from the rows that own them.</summary>
    public class JourneyBoardTests
    {
        private static DeedContext Deeds(int bossMask, int bestLowest) => new DeedContext(
            Level: 90, HasWeaponEquipped: true, LarderStocked: 3, WoodStock: 0, ItemsCrafted: 0, TotalKills: 0,
            BossesSlain: 0, RegionsCompleted: 5, HighestUnlockedRegion: 5, DefeatedRegionBossMask: bossMask,
            ForgeFusions: 0, AffixRerolls: 0, HighestRarityOwned: 0, LargestActiveSetBonus: 0, ForgeLevel: 0,
            InnLevel: 0, VillageBuildingLevelTotal: 0, WarehouseLevel: 0, GatheringMasteryTotal: 0,
            LowestRegionOneKillCount: 0, BestCodexRegionCompletion: 0, BestSeasonRank: 0, ChildrenBred: 0,
            EpicChildrenBred: 0, BestAptitudeTotal: 0, BestLowestAptitude: bestLowest);

        [Fact]
        public void AnEndgameAccountSeesEveryTrack_WithItsOwnNumbers()
        {
            var player = new PlayerRecord
            {
                CurrentLevel = 90, RebirthCount = 20,
                // The pre-rework columns: written by nothing, and must not be read.
                DungeonBestStage = 0, DungeonTickets = 0,
                DelveDeepestFloor = 10, SealsEarnedMask = 0b11,
                CompletedRebirthTrials = RebirthTrials.BitOf(RebirthTrials.Want),
                LegacyPerks = LegacyPerkResolver.SetPerkRank(0L, LegacyPerkResolver.XpMultiplierBitOffset, 3),
            };
            var gates = new[] { new DungeonGateStanding(5, 0), new DungeonGateStanding(8, 0), new DungeonGateStanding(9, 4) };
            var rows = JourneyBoard.Build(Deeds(0b11111, 19), player, 55_455, gates, ascensionSteps: 2);

            Assert.Equal(new[] { "dungeon", "delve", "ascension", "deeds", "bloodline", "trials", "boons" }, rows.Select(r => r.Key));
            var dungeon = rows.Single(r => r.Key == "dungeon");
            Assert.Equal(22, dungeon.Value);
            Assert.Equal(45, dungeon.Max);
            Assert.Contains("5 / 8 / 9", dungeon.Progress);
            Assert.Contains("4 tickets", dungeon.Hint);
            Assert.Equal(2, rows.Single(r => r.Key == "deeds").Value);
            Assert.Equal(19, rows.Single(r => r.Key == "bloodline").Value);
            Assert.Equal(1, rows.Single(r => r.Key == "trials").Value);
            Assert.Equal(3, rows.Single(r => r.Key == "boons").Value);
            Assert.Contains("55,455 shards", rows.Single(r => r.Key == "boons").Hint);
            Assert.Equal(2, rows.Single(r => r.Key == "ascension").Value);
            Assert.Equal(50, rows.Single(r => r.Key == "ascension").Max);
            Assert.All(rows, r => Assert.InRange(r.Value, 0, r.Max));
        }

        [Fact]
        public void TheDungeonRowWaitsForTheGateToOpen()
        {
            var rows = JourneyBoard.Build(Deeds(0, 4), new PlayerRecord { CurrentLevel = 45 }, 0, new[] { new DungeonGateStanding(0, 4) });
            Assert.DoesNotContain(rows, r => r.Key == "dungeon");
        }

        [Fact]
        public void AReturnedExpeditionLeadsTheRoad()
        {
            var rows = JourneyBoard.Build(Deeds(0, 4), new PlayerRecord { CurrentLevel = 60 }, 0, new[] { new DungeonGateStanding(0, 4) },
                ascensionSteps: 0, expeditionSlots: 3, expeditionsAway: 2, expeditionsBack: 1);
            var first = rows.First();
            Assert.Equal("expeditions", first.Key);
            Assert.Equal(2, first.Value);
            Assert.Equal(3, first.Max);
            Assert.Contains("claim", first.Hint);

            var none = JourneyBoard.Build(Deeds(0, 4), new PlayerRecord { CurrentLevel = 60 }, 0, new[] { new DungeonGateStanding(0, 4) });
            Assert.DoesNotContain(none, r => r.Key == "expeditions");
        }
    }
}
