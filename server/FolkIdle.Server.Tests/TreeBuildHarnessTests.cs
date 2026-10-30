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
            // Every boss behind the region is down, as it is for anyone farming
            // there, so the region is the highest unlocked one (Know Your Prey)
            // and earlier bosses are farms. Today's tree reads neither.
            for (int r = RaceUnlockRegistry.FirstRegion; r < region; r++)
                payload.DefeatedRegionBossMask = BossFirstClearRules.MarkDefeated(
                    payload.DefeatedRegionBossMask, RaceUnlockRegistry.GetRegionBossMonsterId(r));
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
        internal readonly record struct Score(double Dps, double SwingsSurvived, bool CanDamage, double KillsPerHour = 0);

        /// <summary>
        /// Kills an hour from the farm fight's exact swing distribution
        /// (FarmKill.Mean swings at the player's interval).
        /// </summary>
        /// <remarks>
        /// Modul: THE SECOND OBJECTIVE (slice 2.4). Damage per second cannot see
        /// First Strike or Executioner: they shorten a kill without raising the
        /// mean swing, so a DPS search never buys them. Kills an hour sees them,
        /// and it is what the walls and the loot actually pay on.
        /// </remarks>
        /// Past 60 expected swings the projection drops the distribution and
        /// tracks health (FarmKill is null), so the mean is health over the
        /// mean swing there - not zero kills.
        private static double KillsPerHour(in HuntingProjection.FightSetup setup)
        {
            if (setup.PlayerIntervalMs <= 0) return 0;
            double swings = setup.FarmKill is { } kill && kill.Mean > 0 ? kill.Mean
                : setup.Swing.Mean > 0 ? setup.FarmMilliHp / setup.Swing.Mean : 0;
            return swings > 0 ? 3_600_000.0 / (swings * setup.PlayerIntervalMs) : 0;
        }

        internal static Score Measure(TickStatePayload payload, byte[] levels, int monsterId)
        {
            for (int id = 0; id < SkillTreeRegistry.NodeCount; id++) SimulationEngine.SetSkillTreeLevel(ref payload, id, levels[id]);
            var setup = HuntingProjection.FightSetup.For(in payload, monsterId, timedEffects: false);
            if (!setup.CanDamage) return new Score(0, 0, false);
            double dps = setup.Swing.Mean * 1000.0 / setup.PlayerIntervalMs;
            double survived = setup.FarmMonsterSwing > 0 ? setup.MaxMilliHp / setup.FarmMonsterSwing : double.PositiveInfinity;
            return new Score(dps, survived, true, KillsPerHour(in setup));
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
            => BestCombatBuild(region, budget, sc => sc.Dps);

        internal static (byte[] Levels, Score Estimate) BestCombatBuild(int region, int budget, Func<Score, double> objective)
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
                    double gain = (objective(estimate) - objective(best)) / objective(best);
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
            return new Score(dps, survived, true, KillsPerHour(in setup));
        }

        /// <summary>The same greedy yardstick as BestCombatBuild, on the War table.</summary>
        /// <remarks>
        /// Modul: A MOVE MAY BE A PATH (slice 2.4). One rank at a time, the
        /// search stopped wherever the next row's gate needed points in a weak
        /// node: no single rank paid, so it never reached the keystones. A move
        /// is now also "fill the column to the gate, then take one rank of the
        /// gated node", filled with the ranks that paid best one at a time, and
        /// judged by its whole gain over its whole cost.
        /// </remarks>
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
                var single = new Dictionary<int, double>();
                var gated = new List<WarNodeDef>();

                void Consider(byte[] target, int cost)
                {
                    var score = MeasureWar(payload, target, monster);
                    if (!score.CanDamage) return;
                    double value = (objective(score) - objective(best)) / objective(best) / cost;
                    if (value > pickValue) { pick = target; pickScore = score; pickCost = cost; pickValue = value; }
                }

                foreach (var node in WarTreeRegistry.All())
                {
                    var target = (byte[])ranks.Clone();
                    if (target[node.Id] >= node.MaxLevel) continue;
                    target[node.Id]++;
                    if (WarTreeRegistry.PlanReason(ranks, target, left, out int cost) != null || cost <= 0)
                    {
                        if (WarTreeRegistry.PlanReason(ranks, target, int.MaxValue, out _) != null) gated.Add(node);
                        continue;
                    }
                    var score = MeasureWar(payload, target, monster);
                    if (!score.CanDamage) continue;
                    single[node.Id] = (objective(score) - objective(best)) / objective(best) / cost;
                    Consider(target, cost);
                }

                foreach (var node in gated)
                {
                    var path = PathTo(ranks, node, single, left, out int cost);
                    if (path != null) Consider(path, cost);
                }

                if (pick == null) break;
                ranks = pick;
                best = pickScore;
                left -= pickCost;
            }
            return (ranks, best);
        }

        /// <summary>
        /// The ranks that open a gated node, plus one rank of it: the column
        /// filled from the ranks that paid best alone, cheapest first on a tie.
        /// Null when the budget cannot reach it or a fork forbids it.
        /// </summary>
        private static byte[]? PathTo(byte[] ranks, WarNodeDef node, Dictionary<int, double> single, int left, out int cost)
        {
            cost = 0;
            var t = (byte[])ranks.Clone();
            for (int guard = 0; guard < 128; guard++)
            {
                var plus = (byte[])t.Clone();
                plus[node.Id]++;
                if (WarTreeRegistry.PlanReason(ranks, plus, left, out cost) == null) return plus;

                bool bridge = node.Kind == "Bridge";
                int gate = bridge ? WarTreeRegistry.BridgeGate : WarTreeRegistry.RowGates[node.Row];
                int column = node.Column;
                if (bridge && WarTreeRegistry.PointsInColumn(t, node.Column) >= gate) column = node.BridgeColumn;
                if (WarTreeRegistry.PointsInColumn(t, column) >= gate) return null;

                WarNodeDef? filler = null;
                double fillerValue = double.NegativeInfinity;
                foreach (var n in WarTreeRegistry.All())
                {
                    if (n.Column != column || n.Kind == "Bridge" || n.Id == node.Id || t[n.Id] >= n.MaxLevel) continue;
                    var step = (byte[])t.Clone();
                    step[n.Id]++;
                    if (WarTreeRegistry.PlanReason(ranks, step, left, out _) != null) continue;
                    double v = single.TryGetValue(n.Id, out double sv) ? sv : -1.0 / n.CostPerRank;
                    if (v > fillerValue || (v == fillerValue && filler != null && n.CostPerRank < filler.CostPerRank))
                    {
                        filler = n;
                        fillerValue = v;
                    }
                }
                if (filler == null) return null;
                t[filler.Id]++;
            }
            return null;
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

        /// <summary>
        /// Slice 2.4's table: the whole War half (mechanics and bridges wired)
        /// against today's tree, on both objectives, at each band's budget.
        /// The target is the War best within ±5% of today's best at every band.
        /// </summary>
        [Fact]
        public void War_half_against_today()
        {
            if (!Enabled) return;
            var table = new StringBuilder();
            table.AppendLine("War half (all wired) against today's tree. Greedy with path moves; kills/h from FarmKill. Target: War within ±5% of today at every band.");
            table.AppendLine("region | budget | today DPS+ / kills+ | War on DPS: DPS+ / kills+ | War on kills: DPS+ / kills+ (spent) | kills build");
            foreach (int region in new[] { 1, 2, 3, 4, 5 })
            {
                int level = BossGearBenchmark.ReferenceLevelForRegion(region);
                var bare = Measure(Reference(region), new byte[SkillTreeRegistry.NodeCount], Target(region));
                foreach (int budget in region == 5 ? new[] { level - 1, 129 } : new[] { level - 1 })
                {
                    var (_, todayDps) = BestCombatBuild(region, budget, sc => sc.Dps);
                    var (_, todayKills) = BestCombatBuild(region, budget, sc => sc.KillsPerHour);
                    var (_, warDps) = BestWarBuild(region, budget, sc => sc.Dps);
                    var (ranks, warKills) = BestWarBuild(region, budget, sc => sc.KillsPerHour);
                    int spent = 0;
                    foreach (var node in WarTreeRegistry.All()) spent += ranks[node.Id] * node.CostPerRank;
                    table.AppendLine(FormattableString.Invariant(
                        $"{region} | {budget} | {todayDps.Dps / bare.Dps - 1:+0.0%} / {todayKills.KillsPerHour / bare.KillsPerHour - 1:+0.0%} | {warDps.Dps / bare.Dps - 1:+0.0%} / {warDps.KillsPerHour / bare.KillsPerHour - 1:+0.0%} | {warKills.Dps / bare.Dps - 1:+0.0%} / {warKills.KillsPerHour / bare.KillsPerHour - 1:+0.0%} ({spent}) | {DescribeWar(ranks)}"));
                }
            }
            _out.WriteLine(table.ToString());
        }

        /// <summary>
        /// Each War node alone at its maximum, gates ignored: kills an hour
        /// gained, and per point spent. The tuning table for slice 2.4.
        /// </summary>
        [Fact]
        public void War_node_values()
        {
            if (!Enabled) return;
            var table = new StringBuilder();
            table.AppendLine("Each War node alone at max (gates ignored): kills/h gain, per point. Regions 1, 3, 5.");
            table.AppendLine("node | cost | r1 kills+ | r3 kills+ | r5 kills+ | r5 per point | r5 swings survived+");
            var bare = new Dictionary<int, Score>();
            foreach (int region in new[] { 1, 3, 5 }) bare[region] = MeasureWar(Reference(region), new byte[SkillRankBuffer.Length], Target(region));
            foreach (var node in WarTreeRegistry.All())
            {
                var ranks = WithRanks((node.Id, (byte)node.MaxLevel));
                int cost = node.MaxLevel * node.CostPerRank;
                var g = new Dictionary<int, double>();
                double survived = 0;
                foreach (int region in new[] { 1, 3, 5 })
                {
                    var sc = MeasureWar(Reference(region), ranks, Target(region));
                    g[region] = sc.KillsPerHour / bare[region].KillsPerHour - 1;
                    if (region == 5) survived = sc.SwingsSurvived / bare[5].SwingsSurvived - 1;
                }
                if (g[1] == 0 && g[3] == 0 && g[5] == 0 && Math.Abs(survived) < 1e-9) continue;
                table.AppendLine(FormattableString.Invariant(
                    $"{node.Name} | {cost} | {g[1]:+0.0%} | {g[3]:+0.0%} | {g[5]:+0.0%} | {g[5] / cost:+0.00%} | {survived:+0.0%}"));
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
