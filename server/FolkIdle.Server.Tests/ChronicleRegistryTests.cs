using System;
using System.Linq;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Progression;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The Chronicle pass's numbers, pinned to the table the owner approved on
    /// 2026-11-15 - printed as well, so a change shows its new totals.
    /// </summary>
    public class ChronicleRegistryTests
    {
        private readonly ITestOutputHelper _output;
        public ChronicleRegistryTests(ITestOutputHelper output) => _output = output;

        private static readonly ChronicleSeason Veles = ChronicleRegistry.Season(1);

        [Fact]
        public void TheApprovedSeasonTotals()
        {
            var all = Enumerable.Range(1, ChronicleRegistry.Levels).SelectMany(l => ChronicleRegistry.RewardsFor(l, Veles)).ToList();
            int diamonds = all.Where(r => r.Kind == ChronicleRewardKind.Diamonds).Sum(r => r.Amount);
            int goldMinutes = all.Where(r => r.Kind == ChronicleRewardKind.Gold).Sum(r => r.Amount);
            int ticketSets = all.Where(r => r.Kind == ChronicleRewardKind.Tickets).Sum(r => r.Amount);
            int goldChests = all.Count(r => r.Kind == ChronicleRewardKind.LootChest && r.Amount == (int)LootChestTier.Gold);
            int ancientChests = all.Count(r => r.Kind == ChronicleRewardKind.LootChest && r.Amount == (int)LootChestTier.Ancient);
            int legendaryWardrobe = all.Count(r => r.Kind == ChronicleRewardKind.WardrobeChest && r.Amount == CosmeticRegistry.Legendary);
            _output.WriteLine($"diamonds {diamonds}, gold {goldMinutes / 60.0:0.#} h, tickets {ticketSets}, chests gold {goldChests} ancient {ancientChests} wardrobe {legendaryWardrobe}");

            Assert.Equal(300, diamonds);
            Assert.Equal(25 * 60, goldMinutes);
            Assert.Equal(10, ticketSets);
            Assert.Equal(3, goldChests);
            Assert.Equal(2, ancientChests);
            Assert.Equal(2, legendaryWardrobe);
        }

        [Fact]
        public void TheKeepsakesSitAt25_40_50()
        {
            Assert.Contains(ChronicleRegistry.RewardsFor(25, Veles), r => r.Kind == ChronicleRewardKind.Avatar);
            Assert.Contains(ChronicleRegistry.RewardsFor(40, Veles), r => r.Kind == ChronicleRewardKind.Frame);
            Assert.Contains(ChronicleRegistry.RewardsFor(50, Veles), r => r.Kind == ChronicleRewardKind.Pet);
        }

        [Fact]
        public void EveryLevelPaysSomething()
        {
            foreach (int l in Enumerable.Range(1, ChronicleRegistry.Levels))
            {
                Assert.NotEmpty(ChronicleRegistry.RewardsFor(l, Veles));
                Assert.NotEmpty(ChronicleRegistry.RewardsFor(l, ChronicleRegistry.Season(7)));
            }
        }

        /// <summary>A season with no paintings must not promise a pet or avatar it cannot give.</summary>
        [Fact]
        public void ASeasonWithoutArtPaysChestsInstead()
        {
            var bare = ChronicleRegistry.Season(2);
            Assert.False(bare.HasArt);
            var all = Enumerable.Range(1, ChronicleRegistry.Levels).SelectMany(l => ChronicleRegistry.RewardsFor(l, bare)).ToList();
            Assert.DoesNotContain(all, r => r.Kind is ChronicleRewardKind.Avatar or ChronicleRewardKind.Pet);
            Assert.Equal("Morana's Chronicle", bare.Name);
        }

        [Fact]
        public void LevelsAndOverflow()
        {
            Assert.Equal(0, ChronicleRegistry.LevelFor(99));
            Assert.Equal(1, ChronicleRegistry.LevelFor(100));
            Assert.Equal(50, ChronicleRegistry.LevelFor(5000));
            Assert.Equal(50, ChronicleRegistry.LevelFor(9000));
            Assert.Equal(0, ChronicleRegistry.OverflowChestsFor(5099));
            Assert.Equal(1, ChronicleRegistry.OverflowChestsFor(5100));
            // A perfect season is 30 x 200 XP = 6,000: ten bonus chests at most.
            Assert.Equal(10, ChronicleRegistry.OverflowChestsFor(30 * 200));
        }

        [Fact]
        public void SeasonOneStartsOn15November2026_AndSeasonsAre30Days()
        {
            int day = (int)(new DateTime(2026, 11, 15) - new DateTime(1970, 1, 1)).TotalDays;
            Assert.Equal(ChronicleRegistry.FirstSeasonDayKey, day);
            Assert.Equal(1, ChronicleRegistry.SeasonNumberFor(day));
            Assert.Equal(1, ChronicleRegistry.SeasonNumberFor(day + 29));
            Assert.Equal(2, ChronicleRegistry.SeasonNumberFor(day + 30));
            Assert.Equal(13, ChronicleRegistry.SeasonNumberFor(day + 12 * 30));
            // The names cycle after twelve.
            Assert.Equal("Veles' Chronicle", ChronicleRegistry.Season(13).Name);
        }

        [Fact]
        public void TheSeasonKeepsakesAreRegistered()
        {
            Assert.NotNull(PetRegistry.Find(Veles.PetId));
            Assert.Equal(ChronicleRegistry.PetPct, PetRegistry.Find(Veles.PetId)!.Bonuses.Single().Pct);
            Assert.Contains(CosmeticRegistry.All, c => c.Id == Veles.AvatarId && c.Bound && c.Kind == CosmeticKind.Avatar);
            Assert.Contains(CosmeticRegistry.All, c => c.Id == Veles.FrameId && c.Bound && c.Kind == CosmeticKind.Frame);
        }

        /// <summary>The plan's promise: every mission on 25 of the 30 days reaches level 50.</summary>
        [Fact]
        public void TwentyFiveFullMissionDaysReachTheEnd()
        {
            int fullDay = MissionRegistry.MissionsPerDay * MissionRegistry.XpPerMission + MissionRegistry.XpAllDoneBonus;
            Assert.Equal(ChronicleRegistry.Levels, ChronicleRegistry.LevelFor(25 * fullDay));
            Assert.True(ChronicleRegistry.LevelFor(24 * fullDay) < ChronicleRegistry.Levels);
        }
    }
}
