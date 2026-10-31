using System.Linq;
using System.Text;
using FolkIdle.Server.Domain.Combat.WorldBossStrike;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;
using Xunit;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The numbers behind Chernobog's rewards, pinned without a database
    /// (spec 2026-10-13). Each assert is a decision the owner made, or a limit
    /// some other part of the game depends on.
    /// </summary>
    public class WorldBossRewardTests
    {
        [Fact]
        public void TheMilestonesPayExactlyTheDelvesWeeklyDiamondCeiling()
        {
            // The boss must not quietly out-pay the game's calibrated diamond tap.
            Assert.Equal(DelveRegistry.TapYardstickDiamondsPerWeek, WorldBossRewards.Milestones.Sum(m => m.Diamonds));
        }

        [Fact]
        public void MilestonesClimbAndTheirIndexIsTheirBit()
        {
            var m = WorldBossRewards.Milestones;
            for (int i = 0; i < m.Count; i++) Assert.Equal(i, m[i].Index);
            for (int i = 1; i < m.Count; i++) Assert.True(m[i].Damage > m[i - 1].Damage);
            Assert.Equal(PetRegistry.BlackSerpentId, m[^1].PetId);
        }

        [Fact]
        public void EveryRewardTheMilestonesNameExists()
        {
            foreach (var m in WorldBossRewards.Milestones)
            {
                if (m.AvatarId != null) Assert.NotNull(CosmeticRegistry.Find(m.AvatarId));
                if (m.TitleSlug != null) Assert.NotNull(TitleRegistry.Find(m.TitleSlug));
                if (m.PetId != null) Assert.NotNull(PetRegistry.Find(m.PetId));
            }
            foreach (var slug in WorldBossRewards.PlaceTitleSlugs) Assert.NotNull(TitleRegistry.Find(slug));
            Assert.Equal(WorldBossRewards.PlaceTitleSlugs.Count, WorldBossRewards.PlaceGoldHours.Count);
        }

        [Fact]
        public void TheBlackSerpentIsTheStrongestSingleBonusAndNeverBoostsTheBoss()
        {
            var serpent = PetRegistry.Find(PetRegistry.BlackSerpentId)!;
            Assert.Equal(2, serpent.Bonuses.Count);
            Assert.All(serpent.Bonuses, b => Assert.Equal(12, b.Pct));
            Assert.DoesNotContain(serpent.Bonuses, b => b.Stat == PetStat.WorldBossDamage);
            int strongestElsewhere = PetRegistry.All.Where(p => p.Id != serpent.Id).SelectMany(p => p.Bonuses).Max(b => b.Pct);
            Assert.True(12 > strongestElsewhere);
        }

        [Fact]
        public void EveryTauntFitsAChatPacketWithTheLongestName()
        {
            string longest = new string('W', 20);
            for (int i = 0; i < WorldBossRewards.SandbaggerTaunts.Count; i++)
            {
                string line = WorldBossRewards.Taunt(longest, i);
                Assert.DoesNotContain("{name}", line);
                Assert.True(Encoding.UTF8.GetByteCount(line) <= ResponseChatMessagePacket.MessageCapacity, line);
            }
        }

        [Theory]
        [InlineData(0.0, 2, 3.0, 3, true, true)]    // threw Monday away, aced Wednesday on, and knew how
        [InlineData(0.0, 2, 3.0, 3, false, false)]  // the same curve from a player still learning: never
        [InlineData(1.0, 2, 3.0, 3, true, false)]   // a real Monday score
        [InlineData(0.0, 2, 1.0, 2, true, false)]   // too few later strikes to say
        [InlineData(0.0, 0, 3.0, 3, true, false)]   // auto-struck the registration: not a choice to play badly
        [InlineData(0.0, 2, 1.0, 3, true, false)]   // poor all week
        public void TheSandbaggingRuleNeedsAllThree(double regSum, int regPlayed, double laterSum, int laterPlayed, bool proven, bool flagged)
        {
            Assert.Equal(flagged, WorldBossHpRules.IsSandbagging(regSum, regPlayed, laterSum, laterPlayed, proven));
        }

        [Fact]
        public void TheHealthIsTheSumOfDailyAveragesTimesN()
        {
            var regs = new[] { new Registration(30_000, 2), new Registration(6_000, 1), new Registration(0, 0) };
            Assert.Equal(126_000, WorldBossHpRules.TotalHp(regs, 6.0));
            Assert.Equal(0, WorldBossHpRules.TotalHp(System.Array.Empty<Registration>(), 6.0));
            for (int i = 0; i < 200; i++) Assert.InRange(WorldBossHpRules.DrawFactor(), WorldBossHpRules.MinFactor, WorldBossHpRules.MaxFactor);
        }

        [Theory]
        [InlineData(0L, 60, GoldRewardScale.FloorGold)]
        [InlineData(8_000_000L, 60, 8_000_000L)]
        [InlineData(8_000_000L, 15, 2_000_000L)]
        [InlineData(10_000L, 15, GoldRewardScale.FloorGold)]
        [InlineData(8_000_000L, 0, 0L)]
        public void ScaledGoldIsMinutesOfIncomeAboveAFloor(long hourly, int minutes, long gold)
        {
            Assert.Equal(gold, GoldRewardScale.ForMinutes(hourly, minutes));
        }

        [Theory]
        [InlineData(0, 1)]
        [InlineData(1, 2)]
        [InlineData(2, 3)]
        [InlineData(9, 3)]
        public void TheLoginRewardGrowsForThreeWeeksThenHolds(int cycles, int multiplier)
        {
            Assert.Equal(multiplier, DailyLoginRewardEngine.WeekMultiplier(cycles));
        }
    }
}
