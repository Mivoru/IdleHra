using System.Collections.Generic;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// The ladder never goes backwards.
    ///
    /// Every previous shape sized a monster as a percentage of the health pool
    /// a player is expected to have in ITS region - 8% for the first, 40% for
    /// the fourth. That reads well and it does not survive the region border,
    /// because the pool grows about twice per region while the percentage
    /// resets fivefold downwards. The product is a cliff: Death Knight hit for
    /// 1,000 and the very next monster in the game hit for 200.
    ///
    /// So difficulty is a single continuous curve across all twenty regulars
    /// now, and this is the test that keeps it one. It asserts the SHAPE, not
    /// the numbers - anyone is free to retune the steps, and nobody is free to
    /// make progress feel like walking downhill.
    /// </summary>
    public class MonsterLadderTests
    {
        private readonly ITestOutputHelper _output;

        public MonsterLadderTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static bool IsBoss(int monsterId) =>
            (monsterId - ContentRegistry.FirstCanonicalMonsterId) % ContentRegistry.MonstersPerRegion
                == ContentRegistry.MonstersPerRegion - 1;

        /// <summary>
        /// Id, name, HP and attack - read through the same scaled accessors the
        /// combat loop uses, so this measures what a player actually meets
        /// rather than what the JSON happens to say.
        /// </summary>
        private sealed record Rung(int Id, string Name, int MaxHp, int AttackPower);

        private static Rung Read(int id) => new(
            id,
            ContentRegistry.GetMonsterName(id),
            ContentRegistry.GetScaledMonsterMaxHp(id),
            ContentRegistry.GetScaledMonsterAttackPower(id));

        private static List<Rung> Regulars()
        {
            var list = new List<Rung>();
            for (int id = ContentRegistry.FirstCanonicalMonsterId;
                 id < ContentRegistry.FirstCanonicalMonsterId + 5 * ContentRegistry.MonstersPerRegion;
                 id++)
            {
                if (!IsBoss(id)) list.Add(Read(id));
            }
            return list;
        }

        [Fact]
        public void EveryRegularIsStrongerThanTheOneBeforeIt()
        {
            var regulars = Regulars();
            for (int i = 1; i < regulars.Count; i++)
            {
                Rung prev = regulars[i - 1], cur = regulars[i];
                Assert.True(
                    cur.MaxHp > prev.MaxHp,
                    $"{cur.Name} has {cur.MaxHp} HP against {prev.Name}'s {prev.MaxHp} - the ladder drops here");
                Assert.True(
                    cur.AttackPower > prev.AttackPower,
                    $"{cur.Name} hits for {cur.AttackPower} against {prev.Name}'s {prev.AttackPower} - the ladder drops here");
            }
        }

        /// <summary>
        /// Crossing into a new region is the biggest single step in the game.
        ///
        /// This is the specific complaint the rewrite answers: the first
        /// monster of a region has to be a good deal harder than the last
        /// regular of the one before, not a rest stop. A player arrives there
        /// having just beaten a boss, wearing what that region dropped.
        /// </summary>
        [Fact]
        public void CrossingARegionBorderStepsUpHarderThanAnyStepInsideOne()
        {
            var regulars = Regulars();
            const int perRegion = 4;

            for (int region = 1; region < 5; region++)
            {
                Rung last = regulars[region * perRegion - 1];
                Rung first = regulars[region * perRegion];

                double hpStep = (double)first.MaxHp / last.MaxHp;
                double attackStep = (double)first.AttackPower / last.AttackPower;
                _output.WriteLine(
                    $"{last.Name} -> {first.Name}: HP x{hpStep:F2}, attack x{attackStep:F2}");

                Assert.True(hpStep >= 1.4, $"{first.Name} has only {hpStep:F2}x the HP of {last.Name}");
                Assert.True(attackStep >= 1.7, $"{first.Name} hits only {attackStep:F2}x as hard as {last.Name}");

                // Modul: region 1 is exempt as a COMPARISON, not as a rung. Its
                // interior is the steepest in the game on purpose - a starting
                // character's own power multiplies several times inside it,
                // which no later region repeats - so requiring the border to
                // beat it would demand a jump the player cannot match.
                if (region == 1) continue;

                for (int i = region * perRegion + 1; i < (region + 1) * perRegion; i++)
                {
                    double inside = (double)regulars[i].AttackPower / regulars[i - 1].AttackPower;
                    Assert.True(
                        attackStep > inside,
                        $"the border into {first.Name} steps x{attackStep:F2}, but {regulars[i].Name} steps x{inside:F2} inside the region");
                }
            }
        }

        /// <summary>
        /// A boss is its region's capstone, so it is checked against its own
        /// region and against the previous boss - never against the next
        /// region's first monster, which is SUPPOSED to sit below it.
        /// </summary>
        [Fact]
        public void EveryBossToppsItsOwnRegionAndTheBossBeforeIt()
        {
            Rung? previousBoss = null;
            var regulars = Regulars();

            for (int region = 0; region < 5; region++)
            {
                int bossId = ContentRegistry.FirstCanonicalMonsterId
                             + region * ContentRegistry.MonstersPerRegion
                             + ContentRegistry.MonstersPerRegion - 1;
                Rung boss = Read(bossId);
                Rung strongestRegular = regulars[region * 4 + 3];

                Assert.True(boss.MaxHp > strongestRegular.MaxHp, $"{boss.Name} is softer than {strongestRegular.Name}");
                Assert.True(boss.AttackPower > strongestRegular.AttackPower, $"{boss.Name} hits softer than {strongestRegular.Name}");

                if (previousBoss is not null)
                {
                    Assert.True(boss.MaxHp > previousBoss.MaxHp, $"{boss.Name} is softer than {previousBoss.Name}");
                    Assert.True(boss.AttackPower > previousBoss.AttackPower, $"{boss.Name} hits softer than {previousBoss.Name}");
                }
                previousBoss = boss;
            }
        }

        /// <summary>
        /// Rewards follow HP exactly - XP is a fifth of it and gold a
        /// twentieth. That relation is what makes pacing solvable on paper
        /// rather than only by simulation, so a hand edit that breaks it should
        /// be caught here rather than found in a balance argument months later.
        /// </summary>
        [Fact]
        public void RewardsStillTrackHealthExactly()
        {
            for (int id = ContentRegistry.FirstCanonicalMonsterId;
                 id < ContentRegistry.FirstCanonicalMonsterId + 5 * ContentRegistry.MonstersPerRegion;
                 id++)
            {
                var m = ContentRegistry.Monsters[id - 1];
                Assert.Equal(m.MaxHp / 5, m.BaseXpReward);
                Assert.Equal(m.MaxHp / 20, m.BaseGoldReward);
            }
        }
    }
}
