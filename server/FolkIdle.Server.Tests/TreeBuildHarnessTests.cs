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
    /// MEASUREMENT HARNESS for the skill tree rework (docs/design/SKILL_TREE_REWORK.md
    /// section 8). Off by default:
    ///   $env:FOLKIDLE_TREE_HARNESS='1'; dotnet test --filter TreeBuildHarnessTests --logger "console;verbosity=detailed"
    ///
    /// What it answers: how much faster does the BEST combat build of the tree
    /// kill, at a given point budget, than the same character with no tree at
    /// all. The boss walls are calibrated with no tree (BossGearBenchmark only
    /// assumes First Blood), so this ratio is exactly the slack the tree puts
    /// under every wall. A new tree must land its best build within ±5% of the
    /// old one's ratio at every band, or the walls move.
    ///
    /// Reference character: the top of each region's band
    /// (BossGearBenchmark.ReferenceLevelForRegion), Human, that region's gear at
    /// Rare quality with Rare affixes, against the region's fourth ordinary
    /// monster. Every number comes from HuntingProjection, the model offline
    /// catch-up pays out of - so "the tree is worth X%" means X% of real loot.
    ///
    /// "Best" is a greedy search on seconds per kill, one purchase at a time,
    /// where a purchase may also be "open the fork" (root to 5 plus one bough
    /// level) or "take the crown" (bough to 5 plus the crown), so a gated node's
    /// value is visible before its gate is paid. Greedy is not optimal; it is a
    /// fixed, repeatable yardstick, and the new tree is measured by the same one.
    /// </summary>
    public class TreeBuildHarnessTests
    {
        private readonly ITestOutputHelper _out;

        public TreeBuildHarnessTests(ITestOutputHelper output)
        {
            _out = output;
            ContentRegistry.Initialize();
        }

        private static bool Enabled => Environment.GetEnvironmentVariable("FOLKIDLE_TREE_HARNESS") == "1";

        // Canon: five regions of four ordinary monsters and a boss, ids 91-115.
        private static int Target(int region) => 91 + (region - 1) * 5 + 2;

        internal static TickStatePayload Reference(int region)
        {
            int level = BossGearBenchmark.ReferenceLevelForRegion(region);
            RaceAttributeGrowth.GetGrowthPerLevel(RaceIds.Human, out int str, out int dex, out int con, out int lck);
            int levels = level - 1;
            var payload = new TickStatePayload
            {
                CurrentLevel = level,
                STR = str * levels, DEX = dex * levels, CON = con * levels, LCK = lck * levels,
                Slot1_CharacterId = Guid.NewGuid(),
                Slot1_AgePhase = 1,
                Slot1_GeneticVector = RaceIds.Human,
                CachedCodexYieldMultiplier = 1f,
                CachedCodexDamageMultiplier = 1f,
            };
            payload.CachedAffixTotals = BossGearBenchmark.BuildEquippedTotals(
                new ReferenceLoadout(level, region, RarityTier.Rare, AffixRarity.Rare));
            return payload;
        }

        /// <summary>
        /// Expected milli-damage per second and the monster swings the full bar
        /// absorbs, from the same FightSetup the projection simulates.
        /// </summary>
        /// <remarks>
        /// Modul: CONTINUOUS ON PURPOSE. Seconds per kill moves in whole swings,
        /// so a +0.4 crit chance often changes it by nothing at all, and a greedy
        /// search on it stops with most of its budget unspent (the first run of
        /// this harness spent 30 of 99 points). Damage per second never
        /// plateaus.
        /// </remarks>
        internal readonly record struct Score(double Dps, double SwingsSurvived, bool CanDamage);

        internal static Score Measure(TickStatePayload payload, byte[] levels, int monsterId)
        {
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++) SimulationEngine.SetSkillTreeLevel(ref payload, id, levels[id]);
            var setup = HuntingProjection.FightSetup.For(in payload, monsterId, timedEffects: false);
            if (!setup.CanDamage) return new Score(0, 0, false);
            double dps = setup.Swing.Mean * 1000.0 / setup.PlayerIntervalMs;
            double survived = setup.FarmMonsterSwing > 0 ? setup.MaxMilliHp / setup.FarmMonsterSwing : double.PositiveInfinity;
            return new Score(dps, survived, true);
        }

        /// <summary>The moves a greedy step may take: each a whole target, judged by PlanReason.</summary>
        private static IEnumerable<byte[]> Moves(byte[] levels)
        {
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
            {
                var plus = (byte[])levels.Clone();
                if (plus[id] < SkillTreeRegistry.MaxLevelOf(id)) { plus[id]++; yield return plus; }
            }
            for (int root = 0; root < SkillTreeRegistry.RootCount; root++)
            {
                var (a, b) = SkillTreeRegistry.BoughsOfRoot(root);
                foreach (int bough in new[] { a, b })
                {
                    var open = (byte[])levels.Clone();
                    open[root] = (byte)Math.Max((int)open[root], SkillTreeRegistry.BoughRequiresRootLevel);
                    open[bough] = (byte)Math.Max((int)open[bough], 1);
                    yield return open;

                    var crown = (byte[])open.Clone();
                    crown[bough] = (byte)Math.Max((int)crown[bough], SkillTreeRegistry.CrownRequiresBoughLevel);
                    crown[SkillTreeRegistry.CrownOfRoot(root)] = 1;
                    yield return crown;
                }
            }
        }

        internal static (byte[] Levels, Score Estimate) BestCombatBuild(int region, int budget)
        {
            var payload = Reference(region);
            int monster = Target(region);
            var levels = new byte[SkillTreeRegistry.NodeCount];
            var best = Measure(payload, levels, monster);
            int left = budget;

            while (left > 0)
            {
                byte[]? pick = null;
                Score pickEstimate = best;
                int pickCost = 0;
                double pickValue = 0;

                foreach (var target in Moves(levels))
                {
                    if (SkillTreeRegistry.PlanReason(levels, target, left, out int cost) != null || cost <= 0) continue;
                    var estimate = Measure(payload, target, monster);
                    if (!estimate.CanDamage) continue;
                    double gain = (estimate.Dps - best.Dps) / best.Dps;
                    double value = gain / cost;
                    if (value > pickValue)
                    {
                        pick = target;
                        pickEstimate = estimate;
                        pickCost = cost;
                        pickValue = value;
                    }
                }

                if (pick == null) break;
                levels = pick;
                best = pickEstimate;
                left -= pickCost;
            }

            return (levels, best);
        }

        private static string Describe(byte[] levels)
        {
            var parts = new List<string>();
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++)
                if (levels[id] > 0) parts.Add($"{SkillTreeRegistry.GetName(id)} {levels[id]}");
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        [Fact]
        public void Baseline_today_tree_best_combat_build()
        {
            if (!Enabled) return;
            var table = new StringBuilder();
            table.AppendLine("Today's tree, best combat build by greedy search on expected damage per second (HuntingProjection.FightSetup, region's third ordinary monster).");
            table.AppendLine("region | level | budget | spent | DPS + | swings survived (bare -> best) | build");

            foreach (int region in new[] { 1, 2, 3, 4, 5 })
            {
                int level = BossGearBenchmark.ReferenceLevelForRegion(region);
                var bare = Measure(Reference(region), new byte[SkillTreeRegistry.NodeCount], Target(region));
                // Points at the band top: one a level, plus the seals a player
                // at that stage plausibly holds; and the end-game 129.
                var budgets = new SortedSet<int> { Math.Max(1, level - 1), Math.Max(1, level - 1) + Math.Min(10, 2 * (region - 1)) };
                if (region == 5) budgets.Add(129);

                foreach (int budget in budgets)
                {
                    var (levels, est) = BestCombatBuild(region, budget);
                    double gain = est.Dps / bare.Dps - 1.0;
                    var none = new byte[SkillTreeRegistry.NodeCount];
                    SkillTreeRegistry.PlanReason(none, levels, int.MaxValue, out int spent);
                    table.AppendLine(FormattableString.Invariant(
                        $"{region} | {level} | {budget} | {spent} | {gain:+0.0%;-0.0%} | {bare.SwingsSurvived:0.0} -> {est.SwingsSurvived:0.0} | {Describe(levels)}"));
                }
            }

            _out.WriteLine(table.ToString());
        }

        // ---- the War half (rework slice 2.2) -----------------------------------

        internal static Score MeasureWar(TickStatePayload payload, byte[] ranks, int monsterId)
        {
            payload.SkillTreeVersion = 1;
            for (int id = 0; id < SkillRankBuffer.Length; id++) payload.SkillRanks[id] = ranks[id];
            var setup = HuntingProjection.FightSetup.For(in payload, monsterId, timedEffects: false);
            if (!setup.CanDamage) return new Score(0, 0, false);
            double dps = setup.Swing.Mean * 1000.0 / setup.PlayerIntervalMs;
            double survived = setup.FarmMonsterSwing > 0 ? setup.MaxMilliHp / setup.FarmMonsterSwing : double.PositiveInfinity;
            return new Score(dps, survived, true);
        }

        /// <summary>The same greedy yardstick as BestCombatBuild, on the War table.</summary>
        internal static (byte[] Ranks, Score Estimate) BestWarBuild(int region, int budget, Func<Score, double> objective)
        {
            var payload = Reference(region);
            int monster = Target(region);
            var ranks = new byte[SkillRankBuffer.Length];
            var best = MeasureWar(payload, ranks, monster);
            int left = budget;

            while (left > 0)
            {
                byte[]? pick = null;
                Score pickScore = best;
                int pickCost = 0;
                double pickValue = 0;
                foreach (var node in WarTreeRegistry.All())
                {
                    var target = (byte[])ranks.Clone();
                    if (target[node.Id] >= node.MaxLevel) continue;
                    target[node.Id]++;
                    if (WarTreeRegistry.PlanReason(ranks, target, left, out int cost) != null || cost <= 0) continue;
                    var score = MeasureWar(payload, target, monster);
                    if (!score.CanDamage) continue;
                    double value = (objective(score) - objective(best)) / objective(best) / cost;
                    if (value > pickValue) { pick = target; pickScore = score; pickCost = cost; pickValue = value; }
                }
                if (pick == null) break;
                ranks = pick;
                best = pickScore;
                left -= pickCost;
            }
            return (ranks, best);
        }

        private static string DescribeWar(byte[] ranks)
        {
            var parts = new List<string>();
            foreach (var node in WarTreeRegistry.All())
                if (ranks[node.Id] > 0) parts.Add($"{node.Name} {ranks[node.Id]}");
            return parts.Count == 0 ? "-" : string.Join(", ", parts);
        }

        /// <summary>
        /// The War half's STAT nodes only (slice 2.2: its mechanics are pending
        /// and cannot be bought), beside today's tree at the same budgets.
        /// </summary>
        [Fact]
        public void War_half_stat_nodes_against_today()
        {
            if (!Enabled) return;
            var table = new StringBuilder();
            table.AppendLine("War half, stat nodes only (mechanics pending), greedy on DPS; then greedy on DPS x swings survived. Same character and target as the baseline.");
            table.AppendLine("region | budget | today DPS+ | War DPS+ (spent) | War balanced: DPS+ / survival+ | DPS build");
            foreach (int region in new[] { 1, 2, 3, 4, 5 })
            {
                int level = BossGearBenchmark.ReferenceLevelForRegion(region);
                var bare = Measure(Reference(region), new byte[SkillTreeRegistry.NodeCount], Target(region));
                foreach (int budget in region == 5 ? new[] { level - 1, 129 } : new[] { level - 1 })
                {
                    var (_, today) = BestCombatBuild(region, budget);
                    var (ranks, war) = BestWarBuild(region, budget, sc => sc.Dps);
                    var (_, balanced) = BestWarBuild(region, budget, sc => sc.Dps * sc.SwingsSurvived);
                    int spent = 0;
                    foreach (var node in WarTreeRegistry.All()) spent += ranks[node.Id] * node.CostPerRank;
                    table.AppendLine(FormattableString.Invariant(
                        $"{region} | {budget} | {today.Dps / bare.Dps - 1:+0.0%} | {war.Dps / bare.Dps - 1:+0.0%} ({spent}) | {balanced.Dps / bare.Dps - 1:+0.0%} / {balanced.SwingsSurvived / bare.SwingsSurvived - 1:+0.0%} | {DescribeWar(ranks)}"));
                }
            }
            _out.WriteLine(table.ToString());
        }

        /// <summary>ALWAYS ON: today's players are untouched - War ranks on a version-0 payload do nothing.</summary>
        [Fact]
        public void War_ranks_do_nothing_on_todays_tree()
        {
            var payload = Reference(3);
            var bare = Measure(payload, new byte[SkillTreeRegistry.NodeCount], Target(3));
            payload.SkillRanks[WarTreeRegistry.Edge] = 5;
            payload.SkillRanks[WarTreeRegistry.KeenEye] = 5;
            payload.SkillRanks[WarTreeRegistry.Vitality] = 5;
            var still = Measure(payload, new byte[SkillTreeRegistry.NodeCount], Target(3));
            Assert.Equal(bare.Dps, still.Dps, 6);
            Assert.Equal(bare.SwingsSurvived, still.SwingsSurvived, 6);

            var war = MeasureWar(Reference(3), WithRanks((WarTreeRegistry.Edge, 5), (WarTreeRegistry.Vitality, 5)), Target(3));
            Assert.True(war.Dps > bare.Dps && war.SwingsSurvived > bare.SwingsSurvived);
        }

        private static byte[] WithRanks(params (int Id, byte Rank)[] set)
        {
            var r = new byte[SkillRankBuffer.Length];
            foreach (var (id, rank) in set) r[id] = rank;
            return r;
        }

        /// <summary>ALWAYS ON: the harness itself works and the tree is worth something.</summary>
        [Fact]
        public void The_tree_makes_a_region_one_character_kill_faster()
        {
            var bare = Measure(Reference(1), new byte[SkillTreeRegistry.NodeCount], Target(1));
            var (_, est) = BestCombatBuild(1, 15);
            Assert.True(bare.CanDamage && est.Dps > bare.Dps,
                $"no tree {bare.Dps:0} milli-DPS, best 15-point build {est.Dps:0}");
        }
    }
}
