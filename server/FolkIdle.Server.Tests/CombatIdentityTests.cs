using System;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// DOES A MONSTER HAVE AN IDENTITY? - 2026-09-06.
    ///
    /// Reported as "I deal 73,500 damage to all monsters, I thought it should
    /// scale and my damage should be lower against the higher difficulty
    /// monsters in that location". Both halves were true and both had a cause:
    ///
    ///   * Monster armour was `10 * regionTier` and its halving constant is
    ///     `30 * regionTier`. The tier cancels out of `A / (K + A)`, so every
    ///     monster in the game mitigated exactly 25.0% - a stat that was
    ///     authored, validated and read on every swing, and could not
    ///     distinguish a Field Mouse from Malakor.
    ///   * DodgeRating was 0 on all twenty-five canonical monsters, so HitChance
    ///     was pinned at its 0.95 ceiling and DEX bought nothing.
    ///   * The codex damage multiplier was `1 + 0.01 * levelSum`, uncapped, and
    ///     stood at 142.8x - so gear, affixes, sets and the entire rarity ladder
    ///     were together 0.7% of the player's damage and nothing underneath it
    ///     could be felt anyway.
    ///
    /// This file prints what a monster is now and fails if any of the three
    /// comes back.
    /// </summary>
    public class CombatIdentityTests
    {
        private readonly ITestOutputHelper _o;

        public CombatIdentityTests(ITestOutputHelper o)
        {
            _o = o;
            ContentRegistry.Initialize();
        }

        private static MonsterDefinition MonsterAt(int region, int rank)
            => ContentRegistry.Monsters[
                ContentRegistry.FirstCanonicalMonsterId + (region - 1) * ContentRegistry.MonstersPerRegion + rank - 1];

        /// <summary>Fraction of a raw swing that survives armour.</summary>
        private static double Through(in MonsterDefinition m)
        {
            long raw = 1_000_000L;
            return CombatDamageModel.Mitigate(
                raw, m.Armor, CombatDamageModel.MonsterArmourHalvingConstant(m.RegionTier)) / (double)raw;
        }

        /// <summary>Hit chance for a character who levelled through this region.</summary>
        private static double Hit(in MonsterDefinition m)
        {
            double accuracy = MonsterDefenceCurve.AccuracyBaselineFor(m.RegionTier);
            return Math.Clamp(accuracy / (100.0 + m.DodgeRating), 0.05, 0.95);
        }

        [Fact]
        public void EveryMonsterUsedToMitigateExactlyTheSameAndNoLongerDoes()
        {
            _o.WriteLine("region rank  name                  armour  dodge   hit   through  effective");
            var effectiveness = new double[6, 5];

            for (int region = 1; region <= 5; region++)
            {
                for (int rank = 0; rank < ContentRegistry.MonstersPerRegion; rank++)
                {
                    var m = MonsterAt(region, rank);
                    double through = Through(in m);
                    double hit = Hit(in m);
                    effectiveness[region, rank] = through * hit;

                    _o.WriteLine(
                        $"{region,6} {(rank == 4 ? "BOSS" : "reg" + rank),5}  {ContentRegistry.GetMonsterName(m.Id),-20} "
                        + $"{m.Armor,6} {m.DodgeRating,6}  {hit,5:F2}  {through,7:F3}  {through * hit,9:F3}");
                }
            }

            // The defect, stated as the test that would have caught it: the
            // twenty-five canonical monsters must not all mitigate the same.
            var mitigations = Enumerable.Range(1, 5)
                .SelectMany(r => Enumerable.Range(0, ContentRegistry.MonstersPerRegion)
                    .Select(rank => Math.Round(Through(MonsterAt(r, rank)), 3)))
                .Distinct()
                .ToList();
            Assert.True(mitigations.Count > 1,
                "every canonical monster mitigates the same fraction - armour is cancelling against its own halving constant again.");

            // And they must not all be equally easy to hit.
            var dodges = Enumerable.Range(1, 5)
                .SelectMany(r => Enumerable.Range(0, ContentRegistry.MonstersPerRegion)
                    .Select(rank => MonsterAt(r, rank).DodgeRating))
                .Distinct()
                .ToList();
            Assert.True(dodges.Count > 1, "every canonical monster has the same DodgeRating.");
            Assert.DoesNotContain(dodges, d => d < 0);
        }

        [Fact]
        public void DamageFallsAsAPlayerWalksDeeperIntoALocation()
        {
            // Modul: the player's own words - "my damage should be lower to the
            // higher difficulty monsters in that location". It was identical to
            // all four. This is that sentence as an assertion.
            for (int region = 1; region <= 5; region++)
            {
                double previous = double.MaxValue;
                for (int rank = 0; rank < 4; rank++)
                {
                    var m = MonsterAt(region, rank);
                    double effective = Through(in m) * Hit(in m);

                    Assert.True(effective < previous,
                        $"region {region} rank {rank} ({ContentRegistry.GetMonsterName(m.Id)}) is no tougher to damage than the monster before it.");
                    previous = effective;
                }

                // Worth feeling, not just worth measuring: the deepest regular
                // of a location takes appreciably less of a swing than the first.
                double first = Through(MonsterAt(region, 0)) * Hit(MonsterAt(region, 0));
                double last = Through(MonsterAt(region, 3)) * Hit(MonsterAt(region, 3));
                _o.WriteLine($"region {region}: the fourth regular takes {last / first:P0} of what the first one does");
                Assert.InRange(last / first, 0.5, 0.85);
            }
        }

        [Fact]
        public void TheBossIsHardToHurtRatherThanHardToHit()
        {
            // Stacking evasion AND armour on a monster that already carries five
            // times the health made a boss fight eleven times a regular's.
            // Armour alone keeps it near five, which is a boss rather than a wall.
            for (int region = 1; region <= 5; region++)
            {
                var boss = MonsterAt(region, 4);
                var strongestRegular = MonsterAt(region, 3);

                Assert.True(Through(in boss) < Through(in strongestRegular),
                    $"region {region}'s boss is not the hardest thing in it to hurt.");
                Assert.True(Hit(in boss) > Hit(in strongestRegular),
                    $"region {region}'s boss is harder to HIT than the regular before it - that combination measured badly, see MonsterDefenceCurve.");
            }
        }

        [Fact]
        public void TheCodexDamageMultiplierDiminishesInsteadOfRunningAway()
        {
            _o.WriteLine("codex levels     old        new");
            foreach (int levelSum in new[] { 0, 10, 100, 500, 2_000, 14_178, 50_000 })
            {
                double old = 1.0 + levelSum * 0.010;
                _o.WriteLine($"{levelSum,12}  {old,8:F2}x  {CodexEngine.DamageMultiplierFor(levelSum),8:F2}x");
            }

            // It never stops paying - this is a curve, not a ceiling, which is
            // the difference between it and the yield multiplier beside it.
            Assert.True(CodexEngine.DamageMultiplierFor(50_000) > CodexEngine.DamageMultiplierFor(14_178));
            Assert.True(CodexEngine.DamageMultiplierFor(14_178) > CodexEngine.DamageMultiplierFor(2_000));

            // The first levels are worth MORE than the linear curve they replace.
            // A curve that bounds the top by punishing the bottom is a different,
            // worse game.
            Assert.True(CodexEngine.DamageMultiplierFor(1) >= 1.0f + 0.010f);

            // And the live account's level sum must land somewhere a fight can be
            // read at. At 142.8x every monster died to one swing; the ladder,
            // rarity, armour and dodge were all invisible underneath it.
            float atLiveMax = CodexEngine.DamageMultiplierFor(14_178);
            Assert.InRange(atLiveMax, 3.0f, 8.0f);

            Assert.Equal(1.0f, CodexEngine.DamageMultiplierFor(0));
            Assert.Equal(1.0f, CodexEngine.DamageMultiplierFor(-5));
        }

        [Fact]
        public void TheHealthBarGrowsOnTheSameShapeAsTheThingHittingIt()
        {
            _o.WriteLine("level   base pool   strongest regular of that region");
            long previous = 0;
            foreach (int level in new[] { 1, 21, 41, 61, 81, 101 })
            {
                long pool = ProgressionEngine.BaseMilliHpForLevel(level) / 1000;
                _o.WriteLine($"{level,5} {pool,11:N0}");
                Assert.True(pool >= previous, "the base health pool went down with level.");
                previous = pool;
            }

            // Level 1 is untouched: nothing about the opening hour moves.
            Assert.Equal(ProgressionEngine.BaseMilliHpAtLevelOne, ProgressionEngine.BaseMilliHpForLevel(1));

            // Modul: THE CURVE HAS TO TRACK MONSTER ATTACK, which is geometric
            // per region. A flat base against a 4.2x-a-region attack curve is
            // what made region 5 a one-shot; a base that grows SLOWER than the
            // attack it faces re-creates that with more steps.
            var firstRegionRegular = ContentRegistry.Monsters[
                ContentRegistry.FirstCanonicalMonsterId + 3 - 1];
            var lastRegionRegular = ContentRegistry.Monsters[
                ContentRegistry.FirstCanonicalMonsterId + 4 * ContentRegistry.MonstersPerRegion + 3 - 1];

            double attackGrowth = (double)lastRegionRegular.AttackPower / firstRegionRegular.AttackPower;
            double poolGrowth = (double)ProgressionEngine.BaseMilliHpForLevel(81) / ProgressionEngine.BaseMilliHpForLevel(1);

            _o.WriteLine($"attack grows {attackGrowth:N0}x from region 1 to 5; the base pool grows {poolGrowth:N0}x over the same levels");
            Assert.True(poolGrowth >= attackGrowth * 0.5,
                $"the health curve ({poolGrowth:N0}x) is falling behind the attack curve ({attackGrowth:N0}x) - this is exactly how region 5 became a one-shot.");
        }
    }
}
