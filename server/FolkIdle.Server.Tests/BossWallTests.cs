using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Modul: THE BOSS WALL, 2026-09-12.
    ///
    /// Reported by the developer playing his own game: "today I have beaten the
    /// tier 4 boss, so out of curiosity I tried to beat Malakor too, and to my
    /// surprise if I didn't stop he would be dead with just all tier 4 gear".
    /// Measured from the live database, the set that was doing it was eight
    /// pieces of RegionTier 4 at quality tiers 8-12 with every affix Legendary.
    /// Malakor is the last monster in the game.
    ///
    /// The wall is the first-clear multiplier, and it was a flat 5x health /
    /// 2x attack for all five region bosses - a single pair of numbers asked to
    /// be a real fight at level 10 and at level 100 against gear that has grown
    /// by two orders of magnitude in between. It is per-region now, and the
    /// numbers are not guesses: this test projects a reference character against
    /// each boss and the multipliers are whatever puts the break-even where the
    /// ladder says it should be.
    ///
    /// THE LADDER (agreed with the developer, see
    /// docs/superpowers/specs/2026-09-12-boss-gear-wall-design.md):
    ///
    ///   region 1 -> quality 4    region 4 -> quality 10, Epic affixes
    ///   region 2 -> quality 7    region 5 -> quality 11, Legendary affixes
    ///   region 3 -> quality 8, Rare affixes
    ///
    /// Region 2 asks for 7 rather than the 6 first sketched: affix COUNT steps at
    /// quality 4, 7, 10 and 13, and tiers 4 and 6 both carry two - so "6 wins, 4
    /// loses" was a 4% tuning window between two mechanically identical sets.
    ///
    /// Region 1 starts low on purpose. A fresh account has to beat that boss to
    /// reach region 2, its Forge ceiling starts at 2, and this repo has already
    /// shipped "a new player died to the first monster and onboarding stalled
    /// there forever" once.
    ///
    /// WHAT THIS TEST CAN AND CANNOT PIN. Quality tiers 10, 11 and 12 all carry
    /// four affixes (RarityTier.GetAffixCount) and sit within about 6% of each
    /// other on RarityTier.PowerMultiplier, so a 10/11 boundary is a hair's
    /// breadth and nothing can make it otherwise. The assertions are therefore
    /// BANDS: the ladder tier wins, two tiers below loses, and a full set one
    /// region behind loses at every tier in the game. The printed table is the
    /// evidence for the shape in between, and a number this test prints in the
    /// band it asserts is a number it checks.
    /// </summary>
    public class BossWallTests
    {
        private readonly ITestOutputHelper _output;

        public BossWallTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static int BossOf(int region) => RaceUnlockRegistry.GetRegionBossMonsterId(region);

        private static ReferenceLoadout Gear(int region, int qualityTier, AffixRarity rarity)
            => new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(region), region, qualityTier, rarity);

        [Fact]
        public void TheRequiredSetBeatsItsBossAndTwoTiersBelowDoesNot()
        {
            var table = new StringBuilder();
            table.AppendLine("boss            req  set                      kill s    die s   outcome");

            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = BossOf(region);
                int required = BossFirstClearRules.RequiredQualityTierFor(region);
                AffixRarity rarity = BossFirstClearRules.RequiredAffixRarityFor(region);

                var atRequirement = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region, required, rarity));
                var belowRequirement = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region, required - 2, rarity));

                table.AppendLine(Describe(bossId, required, $"T{required} r{region} {rarity}", atRequirement));
                table.AppendLine(Describe(bossId, required, $"T{required - 2} r{region} {rarity}", belowRequirement));

                Assert.True(atRequirement.PlayerWins,
                    $"region {region}: the REQUIRED set (region {region}, quality {required}, {rarity} affixes) " +
                    $"must be able to win - it died in {belowRequirement.SecondsToPlayerDeath:F0}s. The wall is a " +
                    "fight, not a brick.");

                Assert.False(belowRequirement.PlayerWins,
                    $"region {region}: quality {required - 2} is two tiers under the bar and still won in " +
                    $"{belowRequirement.SecondsToKillBoss:F0}s, so the requirement is not doing anything.");
            }

            _output.WriteLine(table.ToString());
        }

        /// <summary>
        /// The clause with real teeth. The developer's region-4 set was winning
        /// against a region-5 boss; gear a whole region behind must lose at ANY
        /// quality tier, including tiers above the ladder's own requirement.
        /// </summary>
        [Fact]
        public void AFullSetOneRegionBehindLosesAtEveryQualityTier()
        {
            var table = new StringBuilder();
            table.AppendLine("boss  gear region  tier  kill s     die s    outcome");

            for (int region = RaceUnlockRegistry.FirstRegion + 1; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = BossOf(region);
                AffixRarity rarity = BossFirstClearRules.RequiredAffixRarityFor(region);

                for (int qualityTier = 1; qualityTier <= RarityTier.Transcendent; qualityTier++)
                {
                    var behind = BossGearBenchmark.ProjectFirstClear(bossId, Gear(region - 1, qualityTier, rarity));

                    if (qualityTier % 4 == 0 || qualityTier == RarityTier.Transcendent)
                    {
                        table.AppendLine(Describe(bossId, qualityTier, $"region {region - 1} T{qualityTier}", behind));
                    }

                    Assert.False(behind.PlayerWins,
                        $"the region-{region} boss was beaten by a full set of region-{region - 1} gear at quality " +
                        $"{qualityTier} in {behind.SecondsToKillBoss:F0}s. That is the defect this work exists to " +
                        "fix: the gear of the region BEFORE the boss must not be enough.");
                }
            }

            _output.WriteLine(table.ToString());
        }

        /// <summary>
        /// "A boss sized to need a full set of high-rarity gear is then a wall
        /// every time a player wants the thing it drops, and farming a wall is
        /// not fun, it is a tax." - BossFirstClearRules' own comment, and the
        /// developer's own choice when asked: the wall is the FIRST clear only.
        /// </summary>
        [Fact]
        public void OnceClearedTheBossIsFarmableByWeakerGear()
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = BossOf(region);
                int required = BossFirstClearRules.RequiredQualityTierFor(region);
                AffixRarity rarity = BossFirstClearRules.RequiredAffixRarityFor(region);

                var farming = BossGearBenchmark.ProjectCleared(bossId, Gear(region, required - 2, rarity));

                Assert.True(farming.PlayerWins,
                    $"region {region}: the boss is beaten and is still unbeatable at quality {required - 2}, so " +
                    "farming it for its drops is a tax rather than a fight.");
            }
        }

        /// <summary>
        /// The multiplier may never make a CLEARED boss harder than the content
        /// it is authored as - the wall has to switch off completely.
        /// </summary>
        [Fact]
        public void ClearingTheBossRemovesTheWallEntirely()
        {
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int bossId = BossOf(region);
                byte cleared = BossFirstClearRules.MarkDefeated(0, bossId);

                Assert.Equal(ContentRegistry.GetScaledMonsterMaxHp(bossId), BossFirstClearRules.MaxHpFor(cleared, bossId));
                Assert.Equal(ContentRegistry.GetScaledMonsterAttackPower(bossId), BossFirstClearRules.AttackPowerFor(cleared, bossId));

                // And the wall is a real one while it stands.
                Assert.True(BossFirstClearRules.MaxHpFor(0, bossId) > ContentRegistry.GetScaledMonsterMaxHp(bossId));
            }
        }

        /// <summary>
        /// The ladder may not descend. Every region's bar is at least the
        /// previous region's - a boss deeper in the game cannot ask for less.
        /// </summary>
        [Fact]
        public void TheRequirementLadderNeverDescends()
        {
            int previous = 0;
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                int required = BossFirstClearRules.RequiredQualityTierFor(region);
                Assert.True(required >= previous, $"region {region} asks for quality {required}, less than region {region - 1}'s {previous}.");
                Assert.InRange(required, 1, RarityTier.Transcendent);
                previous = required;
            }

            // Region 1 has to stay reachable by an account that has beaten
            // nothing and owns a level-0 Forge.
            Assert.True(BossFirstClearRules.RequiredQualityTierFor(1) <= 5,
                "the region-1 boss is the entrance to the game. A requirement above quality 5 closes it.");
        }

        private static string Describe(int bossId, int tier, string label, in BossFightProjection p)
        {
            string kill = double.IsInfinity(p.SecondsToKillBoss) ? "never" : $"{p.SecondsToKillBoss,8:F0}";
            string die = double.IsInfinity(p.SecondsToPlayerDeath) ? "never" : $"{p.SecondsToPlayerDeath,7:F0}";
            return $"{ContentRegistry.GetMonsterName(bossId),-14} {tier,3}  {label,-22} {kill} {die}   {(p.PlayerWins ? "WIN" : "lose")}";
        }
    }
}
