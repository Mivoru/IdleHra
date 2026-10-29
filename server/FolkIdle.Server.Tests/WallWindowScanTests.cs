using System;
using System.Collections.Generic;
using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// CALIBRATION HARNESS, off by default. Finds, per region, the window of
    /// first-clear attack multipliers inside which every boss-wall rule in
    /// BossWallTests holds at once:
    ///   - the required gear wins;
    ///   - two quality tiers under it loses;
    ///   - a full set of the region BEFORE loses at every quality tier, bare
    ///     and with either of its sets.
    /// And the seasonal tier-6 AttackPct window: region 5's wall gear loses,
    /// the best gear in the game wins inside an hour.
    ///
    /// BossFirstClearRules quotes these windows ("measured windows ... a
    /// ZzWallScan-style sweep"), but the sweep itself was never committed, so
    /// every recalibration started from nothing. This is that sweep. It costs
    /// tens of thousands of projections, so it runs only when asked:
    ///   $env:FOLKIDLE_WALL_SCAN='1'; dotnet test --filter WallWindowScanTests --logger "console;verbosity=detailed"
    /// </summary>
    public class WallWindowScanTests
    {
        private readonly ITestOutputHelper _out;

        public WallWindowScanTests(ITestOutputHelper output)
        {
            _out = output;
            ContentRegistry.Initialize();
        }

        private static bool Enabled => Environment.GetEnvironmentVariable("FOLKIDLE_WALL_SCAN") == "1";

        private static bool WallHolds(int region, double attack)
        {
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(region);
            int required = BossFirstClearRules.RequiredQualityTierFor(region);
            AffixRarity rarity = BossFirstClearRules.RequiredAffixRarityFor(region);
            int level = BossGearBenchmark.ReferenceLevelForRegion(region);

            if (!BossGearBenchmark.ProjectFirstClearWithAttackMultiplier(bossId, new ReferenceLoadout(level, region, required, rarity), attack).PlayerWins)
                return false;
            if (BossGearBenchmark.ProjectFirstClearWithAttackMultiplier(bossId, new ReferenceLoadout(level, region, required - 2, rarity), attack).PlayerWins)
                return false;
            if (region == RaceUnlockRegistry.FirstRegion) return true;

            int offence = (region - 2) * 2 + 1;
            for (int quality = 1; quality <= RarityTier.Transcendent; quality++)
            {
                foreach (int setId in new[] { offence, offence + 1 })
                {
                    int piece = EquippedSetIds.Pack(setId, quality);
                    var sets = new EquippedSetIds { Helmet = piece, Chest = piece, Gloves = piece, Leggings = piece, Boots = piece };
                    if (BossGearBenchmark.ProjectFirstClearWithAttackMultiplier(bossId, new ReferenceLoadout(level, region - 1, quality, rarity, sets), attack).PlayerWins)
                        return false;
                }
                var behindGear = new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(region - 1), region - 1, quality, rarity);
                if (BossGearBenchmark.ProjectFirstClearWithAttackMultiplier(bossId, behindGear, attack).PlayerWins)
                    return false;
            }
            return true;
        }

        [Fact]
        public void First_clear_attack_windows()
        {
            if (!Enabled) return;
            var table = new StringBuilder();
            table.AppendLine("region | current | window (every wall rule holds) | step 0.25%");
            for (int region = RaceUnlockRegistry.FirstRegion; region <= RaceUnlockRegistry.LastRegion; region++)
            {
                double current = BossFirstClearRules.AttackMultiplierFor(region);
                var holding = new List<double>();
                for (double factor = 0.5; factor <= 2.0001; factor += 0.0025)
                {
                    double attack = Math.Round(current * factor, 3);
                    if (WallHolds(region, attack)) holding.Add(attack);
                }
                string window = holding.Count == 0 ? "NONE" : FormattableString.Invariant($"{holding[0]:0.###} - {holding[^1]:0.###} ({holding.Count} steps)");
                table.AppendLine(FormattableString.Invariant($"{region} | {current:0.###} | {window} | holds at current: {WallHolds(region, current)}"));
            }
            _out.WriteLine(table.ToString());
        }

        [Fact]
        public void Seasonal_tier_six_attack_window()
        {
            if (!Enabled) return;
            int boss5 = RaceUnlockRegistry.GetRegionBossMonsterId(5);
            var wallGear = new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(5), 5,
                BossFirstClearRules.RequiredQualityTierFor(5), BossFirstClearRules.RequiredAffixRarityFor(5));
            var bestGear = new ReferenceLoadout(100, 5, RarityTier.Transcendent, AffixRarity.Legendary);
            var six = SeasonalBossRegistry.Find(6)!.Modifiers;

            var table = new StringBuilder();
            table.AppendLine(FormattableString.Invariant($"tier 6 today: AttackPct {six.AttackPct}, BossHpPct {six.BossHpPct}"));
            table.AppendLine("AttackPct | wall gear kill/death | best gear kill/death | holds");
            for (int attackPct = 0; attackPct <= 120; attackPct += 5)
            {
                var mods = six with { AttackPct = attackPct };
                var w = BossGearBenchmark.ProjectFirstClearWithModifiers(boss5, in wallGear, in mods);
                var b = BossGearBenchmark.ProjectFirstClearWithModifiers(boss5, in bestGear, in mods);
                bool holds = !w.PlayerWins && b.PlayerWins && b.SecondsToKillBoss < 3600;
                table.AppendLine(FormattableString.Invariant(
                    $"{attackPct} | {w.SecondsToKillBoss:F0}/{w.SecondsToPlayerDeath:F0} | {b.SecondsToKillBoss:F0}/{b.SecondsToPlayerDeath:F0} | {holds}"));
            }
            _out.WriteLine(table.ToString());
        }
    }
}
