using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Skill tree v3 step C (owner, 2026-11-14): the balance measurement for
    /// situational builds. Five archetype builds, each spending about the same
    /// points, are scored in each situation the game has, and the table is
    /// PRINTED - it is the measurement, like ProgressionRateTests'. The goal
    /// (docs/design/SKILL_TREE_V3.md) is that every build is best somewhere and
    /// none is best everywhere; step D sets the numbers from this table.
    /// </summary>
    /// <remarks>
    /// Modul: REAL NUMBERS, REAL CODE. The grind and the boss columns run the
    /// per-swing simulator (CombatSimulator, the live tick's own code) for an
    /// hour; the dungeon column runs DungeonSimulator stage by stage until the
    /// first loss; the world boss column is today's strike
    /// (WorldBossTickCoordinator.ServerStrikeDamage) beside the sustained
    /// damage the projection prices against a boss that never dies - the two
    /// differ by design today, which is a finding, not a bug in this test.
    /// The hidden step-B nodes are set directly; TestValueScale is held at 1.
    /// </remarks>
    [Collection("Static loot queues")]
    public class BuildSituationMatrixTests
    {
        private readonly ITestOutputHelper _output;
        private const long Hour = 36_000;
        private const int Region = 5;

        public BuildSituationMatrixTests(ITestOutputHelper output)
        {
            _output = output;
            ContentRegistry.Initialize();
        }

        private static int RegularOf(int region) => ContentRegistry.FirstCanonicalMonsterId + ((region - 1) * 5) + 2;

        /// <summary>Every node of a column up to a row at its limit; forks take the preferred side, else the first.</summary>
        private static void FillColumn(Dictionary<int, byte> ranks, int column, int maxRow, params int[] preferred)
        {
            foreach (var n in WarTreeRegistry.All().Where(n => n.Column == column && n.Kind != "Bridge" && !n.Hidden && n.Row <= maxRow).OrderBy(n => n.Id))
            {
                if (n.Fork >= 0)
                {
                    bool wantThis = preferred.Contains(n.Id) || (!preferred.Contains(n.Fork) && n.Id < n.Fork);
                    if (!wantThis) continue;
                }
                ranks[n.Id] = (byte)n.MaxLevel;
            }
        }

        private static void Max(Dictionary<int, byte> ranks, params int[] ids)
        {
            foreach (int id in ids) ranks[id] = (byte)WarTreeRegistry.Get(id)!.MaxLevel;
        }

        internal static IReadOnlyList<(string Name, Dictionary<int, byte> Ranks)> Archetypes()
        {
            var generalist = new Dictionary<int, byte>();
            for (int c = 0; c < 4; c++) FillColumn(generalist, c, 1);

            var grind = new Dictionary<int, byte>();
            FillColumn(grind, WarTreeRegistry.ColumnFortune, 4);
            FillColumn(grind, WarTreeRegistry.ColumnMight, 2, WarTreeRegistry.Executioner);
            Max(grind, WarTreeRegistry.Momentum, WarTreeRegistry.KnowYourPrey);

            var boss = new Dictionary<int, byte>();
            FillColumn(boss, WarTreeRegistry.ColumnMight, 4, WarTreeRegistry.Bossbreaker);
            FillColumn(boss, WarTreeRegistry.ColumnGuard, 1);
            Max(boss, WarTreeRegistry.Sunder, WarTreeRegistry.PiercingCrit);

            var titan = new Dictionary<int, byte>();
            FillColumn(titan, WarTreeRegistry.ColumnPrecision, 4);
            FillColumn(titan, WarTreeRegistry.ColumnMight, 1);
            Max(titan, WarTreeRegistry.Lacerate, WarTreeRegistry.PiercingCrit);

            var warden = new Dictionary<int, byte>();
            FillColumn(warden, WarTreeRegistry.ColumnGuard, 4);
            FillColumn(warden, WarTreeRegistry.ColumnPrecision, 1);
            FillColumn(warden, WarTreeRegistry.ColumnMight, 0);
            Max(warden, WarTreeRegistry.Counterstroke);

            // Every build spends the same: topped up one rank at a time from the
            // cheap first rows of the other columns, never past Budget.
            foreach (var b in new[] { generalist, grind, boss, titan, warden }) TopUp(b);

            return new[]
            {
                ("Generalist", generalist),
                ("Grind (Fortune+Momentum)", grind),
                ("Bossbreaker (Might+Sunder)", boss),
                ("Titan-slayer (Precision+Bleed)", titan),
                ("Warden (Guard)", warden),
                // Controls: the same three builds without their step-B mechanic.
                ("  control: Grind without Momentum", Without(grind, WarTreeRegistry.Momentum)),
                ("  control: Bossbreaker without Sunder", Without(boss, WarTreeRegistry.Sunder)),
                ("  control: Titan-slayer without Bleed", Without(titan, WarTreeRegistry.Lacerate)),
            };
        }

        internal const int Budget = 88;

        private static Dictionary<int, byte> Without(Dictionary<int, byte> ranks, int id)
        {
            var copy = new Dictionary<int, byte>(ranks);
            copy.Remove(id);
            return copy;
        }

        private static void TopUp(Dictionary<int, byte> ranks)
        {
            var filler = WarTreeRegistry.All()
                .Where(n => !n.Hidden && n.Kind != "Keystone" && n.Kind != "Bridge" && n.Row <= 1 && n.Fork < 0)
                .OrderBy(n => n.Row).ThenBy(n => n.Id).ToList();
            bool added = true;
            while (added && Points(ranks) < Budget)
            {
                added = false;
                foreach (var n in filler)
                {
                    ranks.TryGetValue(n.Id, out byte have);
                    if (have >= n.MaxLevel || Points(ranks) + n.CostPerRank > Budget) continue;
                    ranks[n.Id] = (byte)(have + 1);
                    added = true;
                    if (Points(ranks) >= Budget) break;
                }
            }
        }

        private static int Points(Dictionary<int, byte> ranks)
            => ranks.Sum(kv => kv.Value * WarTreeRegistry.Get(kv.Key)!.CostPerRank);

        private static TickStatePayload Dressed(int monsterId, Dictionary<int, byte> ranks)
        {
            WarTreeRegistry.TestValueScale.Value = 1;
            var p = OfflineCombatParityTests.Character(Region, geared: true, monsterId, food: 20_000);
            return TestTree.With(p, ranks.Select(kv => (kv.Key, kv.Value)).ToArray());
        }

        private sealed record Scores(int Points, double GrindKills, double GrindGold, double BossKills, double DungeonStage, long StrikeToday, double SustainedBossDps);

        private static Scores Score(Dictionary<int, byte> ranks)
        {
            int regular = RegularOf(Region);
            int bossId = RaceUnlockRegistry.GetRegionBossMonsterId(Region);

            // Grind: an hour on the region's regular, three seeds.
            double kills = 0, gold = 0;
            for (int seed = 1; seed <= 3; seed++)
            {
                var p = Dressed(regular, ranks);
                long before = p.CurrentGold;
                var o = CombatSimulator.Run(ref p, Hour, new Random(seed));
                kills += o.Tally.Kills;
                gold += p.CurrentGold - before;
            }

            // The region boss: an hour of it, three seeds.
            double bossKills = 0;
            for (int seed = 1; seed <= 3; seed++)
            {
                var p = Dressed(bossId, ranks);
                bossKills += CombatSimulator.Run(ref p, Hour, new Random(seed)).Tally.Kills;
            }

            // The dungeon: the highest stage won, plus the share of the next boss taken off.
            double stage = 0;
            {
                var p = Dressed(regular, ranks);
                for (int s = 1; s <= 30; s++)
                {
                    var r = DungeonSimulator.Run(p, s, p.Food1_ItemId, p.Food1_Count);
                    if (r.Won) { stage = s; continue; }
                    stage += r.ReachedBoss ? (100 - r.BossHpLeftPct) / 100.0 : 0;
                    break;
                }
            }

            // The world boss: today's strike, and what the swing sustains against a boss that never dies.
            long strike;
            double sustained;
            {
                var p = Dressed(bossId, ranks);
                var stats = SimulationEngine.LiveCombatStats(in p);
                long raw = SimulationEngine.EffectiveMilliAttackFor(ref p, in stats, SimulationEngine.LineageOf(in p).DamageScalePerLevelPct);
                p.CachedEffectiveMilliAttack = raw;
                strike = WorldBossTickCoordinator.ServerStrikeDamage(ref p);
                var monster = ContentRegistry.Monsters[bossId - 1];
                var swing = HuntingProjection.PlayerSwing.For(in p, in stats, in monster, raw, fightMilliHp: 1e13);
                sustained = swing.Mean / 1000.0 * 1000.0 / Math.Max(1, SimulationEngine.LiveAttackIntervalMs(in p, in stats));
            }

            return new Scores(Points(ranks), kills / 3, gold / 3, bossKills / 3, stage, strike, sustained);
        }

        [Fact]
        public void The_build_by_situation_matrix_is_printed()
        {
            var rows = Archetypes().Select(a => (a.Name, S: Score(a.Ranks))).ToList();

            var sb = new StringBuilder();
            sb.AppendLine($"Region {Region}, geared reference character, real values (scale 1). Grind/boss: per hour, mean of 3 seeds.");
            sb.AppendLine("| build | points | grind kills | grind gold | boss kills | dungeon stage | world boss strike today | sustained boss dps |");
            sb.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var (name, s) in rows)
                sb.AppendLine($"| {name} | {s.Points} | {s.GrindKills:F0} | {s.GrindGold:F0} | {s.BossKills:F1} | {s.DungeonStage:F2} | {s.StrikeToday} | {s.SustainedBossDps:F0} |");

            // Who is best where.
            string Best(Func<Scores, double> f) => rows.OrderByDescending(r => f(r.S)).First().Name;
            sb.AppendLine();
            sb.AppendLine($"best grind kills: {Best(s => s.GrindKills)}");
            sb.AppendLine($"best grind gold: {Best(s => s.GrindGold)}");
            sb.AppendLine($"best boss kills: {Best(s => s.BossKills)}");
            sb.AppendLine($"best dungeon: {Best(s => s.DungeonStage)}");
            sb.AppendLine($"best world boss strike today: {Best(s => s.StrikeToday)}");
            sb.AppendLine($"best sustained boss dps: {Best(s => s.SustainedBossDps)}");
            _output.WriteLine(sb.ToString());
            Console.WriteLine(sb.ToString());

            // A measurement, not a balance verdict: every build must at least fight.
            foreach (var (name, s) in rows)
            {
                Assert.True(s.GrindKills > 0, $"{name} killed nothing on the grind");
                Assert.True(s.StrikeToday > 0, $"{name} strikes the world boss for nothing");
                Assert.True(s.SustainedBossDps > 0, $"{name} sustains no damage");
            }
        }
    }
}
