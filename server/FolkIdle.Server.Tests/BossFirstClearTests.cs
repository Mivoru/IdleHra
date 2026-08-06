using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The first kill of a region boss and the hundredth are different fights.
    ///
    /// Sizing a boss so that beating it needs a full set of high-rarity gear
    /// makes the FIRST kill a milestone, which is the point - and makes every
    /// later kill a wall standing between the player and the thing it drops,
    /// which is not. Splitting the two is what lets the milestone be brutal
    /// without the farm being a tax.
    /// </summary>
    public class BossFirstClearTests
    {
        private readonly ITestOutputHelper _output;

        public BossFirstClearTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static int BossOf(int region) => RaceUnlockRegistry.GetRegionBossMonsterId(region);

        [Fact]
        public void AnUnbeatenBossIsFiveTimesTheHealthAndTwiceTheAttack()
        {
            for (int region = 1; region <= 5; region++)
            {
                int bossId = BossOf(region);
                long authoredHp = ContentRegistry.GetScaledMonsterMaxHp(bossId);
                long authoredAttack = ContentRegistry.GetScaledMonsterAttackPower(bossId);

                long firstClearHp = BossFirstClearRules.MaxHpFor(0, bossId);
                long firstClearAttack = BossFirstClearRules.AttackPowerFor(0, bossId);

                _output.WriteLine(
                    $"region {region} boss {ContentRegistry.GetMonsterName(bossId)}: " +
                    $"{authoredHp} HP / {authoredAttack} atk farmed, " +
                    $"{firstClearHp} / {firstClearAttack} on the first clear");

                Assert.Equal(authoredHp * 5, firstClearHp);
                Assert.Equal(authoredAttack * 2, firstClearAttack);
            }
        }

        [Fact]
        public void BeatingABossDropsItBackToItsAuthoredStats()
        {
            int bossId = BossOf(2);
            byte mask = BossFirstClearRules.MarkDefeated(0, bossId);

            Assert.False(BossFirstClearRules.IsFirstClearPending(mask, bossId));
            Assert.Equal(ContentRegistry.GetScaledMonsterMaxHp(bossId), BossFirstClearRules.MaxHpFor(mask, bossId));
            Assert.Equal(ContentRegistry.GetScaledMonsterAttackPower(bossId), BossFirstClearRules.AttackPowerFor(mask, bossId));

            // Beating one boss says nothing about any other.
            Assert.True(BossFirstClearRules.IsFirstClearPending(mask, BossOf(3)));
            Assert.Equal(ContentRegistry.GetScaledMonsterMaxHp(BossOf(3)) * 5, BossFirstClearRules.MaxHpFor(mask, BossOf(3)));
        }

        /// <summary>
        /// The whole reason this reads a bitmask rather than HighestUnlockedRegion.
        ///
        /// Clearing region 5's boss opens no sixth region, so that number stays
        /// at 5 before and after - and the last boss in the game would sit at
        /// first-clear stats forever, unfarmable by the only players who can
        /// reach it.
        /// </summary>
        [Fact]
        public void TheLastBossInTheGameCanAlsoBeFarmedOnceItFalls()
        {
            int lastBoss = BossOf(5);
            Assert.True(BossFirstClearRules.IsFirstClearPending(0, lastBoss));

            byte mask = BossFirstClearRules.MarkDefeated(0, lastBoss);
            Assert.False(BossFirstClearRules.IsFirstClearPending(mask, lastBoss));
            Assert.Equal(ContentRegistry.GetScaledMonsterMaxHp(lastBoss), BossFirstClearRules.MaxHpFor(mask, lastBoss));
        }

        [Fact]
        public void AnOrdinaryMonsterIsNeverAFirstClear()
        {
            // Every regular of region 1, at a completely empty mask.
            for (int id = 91; id <= 94; id++)
            {
                Assert.False(BossFirstClearRules.IsFirstClearPending(0, id));
                Assert.Equal(ContentRegistry.GetScaledMonsterMaxHp(id), BossFirstClearRules.MaxHpFor(0, id));
                Assert.Equal(ContentRegistry.GetScaledMonsterAttackPower(id), BossFirstClearRules.AttackPowerFor(0, id));
            }
        }

        [Fact]
        public void TheMaskRoundTripsThroughTheCodexSetTheUnlockGateReads()
        {
            var defeated = new System.Collections.Generic.HashSet<int> { BossOf(1), BossOf(2) };
            byte mask = BossFirstClearRules.MaskFrom(defeated);

            Assert.True(BossFirstClearRules.IsDefeated(mask, BossOf(1)));
            Assert.True(BossFirstClearRules.IsDefeated(mask, BossOf(2)));
            Assert.False(BossFirstClearRules.IsDefeated(mask, BossOf(3)));
        }
    }
}
