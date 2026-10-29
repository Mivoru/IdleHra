using System;
using System.Text;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// MEASUREMENT, NOT A FIX. Three suspected unit errors from
    /// docs/architecture/STATS_REFERENCE.md §16 (Draugr mastery lifesteal, the
    /// Vila/Draugr ±0.15 attack speed, the defensive potion's tier × 0.01
    /// dodge), each priced through the functions the tick uses
    /// (LiveCombatStats, HuntingProjection.PlayerSwing, LiveAttackIntervalMs,
    /// MonsterHitChance) against candidate values, so the owner picks numbers
    /// from a table rather than from intuition. Nothing here changes a
    /// constant: the candidates are applied to a copy of the computed stats.
    ///
    /// Reference character: the top of each region's band (level 20 × region),
    /// that region's gear at Rare quality with Rare affixes
    /// (BossGearBenchmark.ReferenceLoadout), against the region's third
    /// ordinary monster. The asserts are only sanity floors; the output is the
    /// point - run with `--logger "console;verbosity=detailed"`.
    /// </summary>
    public class RaceUnitMeasurementTests
    {
        private readonly ITestOutputHelper _out;

        public RaceUnitMeasurementTests(ITestOutputHelper output)
        {
            _out = output;
            ContentRegistry.Initialize();
        }

        // Canon: five regions of four ordinary monsters and a boss, ids 91-115.
        private static int MonsterFor(int region) => 91 + (region - 1) * 5 + 2;

        private static TickStatePayload Reference(int region, byte raceId, int mastery)
        {
            int level = BossGearBenchmark.ReferenceLevelForRegion(region);
            RaceAttributeGrowth.GetGrowthPerLevel(raceId, out int str, out int dex, out int con, out int lck);
            int levels = level - 1;
            var payload = new TickStatePayload
            {
                CurrentLevel = level,
                STR = str * levels, DEX = dex * levels, CON = con * levels, LCK = lck * levels,
                Slot1_CharacterId = Guid.NewGuid(),
                Slot1_AgePhase = 1,
                Slot1_GeneticVector = raceId,
                CachedCodexYieldMultiplier = 1f,
                CachedCodexDamageMultiplier = 1f,
            };
            if (raceId == RaceIds.Vila) payload.VilaMasteryLevel = mastery;
            if (raceId == RaceIds.Draugr) payload.DraugrMasteryLevel = mastery;
            payload.CachedAffixTotals = BossGearBenchmark.BuildEquippedTotals(
                new ReferenceLoadout(level, region, RarityTier.Rare, AffixRarity.Rare));
            return payload;
        }

        private static long RawAttack(ref TickStatePayload payload, in CombatStats stats)
            => SimulationEngine.EffectiveMilliAttackFor(ref payload, in stats, SimulationEngine.LineageOf(in payload).DamageScalePerLevelPct);

        [Fact]
        public void Draugr_mastery_lifesteal_candidates()
        {
            var table = new StringBuilder();
            table.AppendLine("Draugr mastery 50 (the code adds 12 points since 2026-10-29; 0.12 before). Heal per second as % of max HP; cap = 1% of max HP per hit.");
            table.AppendLine("region | max HP | mean hit | cap/hit | variant | lifesteal% | raw heal/hit | healed/hit | hits at cap | heal %HP/s | monster %HP/swing");

            var variants = new (string Name, float AddedPct)[] { ("before fix", 0.12f), ("3.5% (alternative)", 3.5f), ("12% (current)", 12f) };
            for (int region = 1; region <= 5; region++)
            {
                var payload = Reference(region, RaceIds.Draugr, 50);
                var computed = SimulationEngine.LiveCombatStats(in payload);
                float otherSources = computed.LifestealPct - RaceMasteryResolver.GetDraugrLifestealBonusPct(50);
                long maxHp = SimulationEngine.EffectiveMaxMilliHpFor(in payload, in computed);
                long cap = maxHp / 100;
                var monster = ContentRegistry.Monsters[MonsterFor(region) - 1];
                long raw = RawAttack(ref payload, in computed);
                int intervalMs = SimulationEngine.LiveAttackIntervalMs(in payload, in computed);
                double monsterPerSwing = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in computed, monster.Id, 0, maxHp);

                foreach (var (name, added) in variants)
                {
                    var stats = computed;
                    stats.LifestealPct = Math.Max(0f, otherSources) + added;
                    var swing = HuntingProjection.PlayerSwing.For(in payload, in stats, in monster, raw);
                    double rawHeal = swing.Mean * stats.LifestealPct / 100.0;
                    double healed = swing.ExpectedHeal(cap);
                    // Share of connecting hits whose lifesteal exceeds the cap,
                    // taken on the three outcomes the swing model has.
                    double pAtCap = 0, pConnect = swing.PNormal + swing.PCrit + swing.PDouble;
                    if (swing.NormalDamage * stats.LifestealPct / 100.0 >= cap) pAtCap += swing.PNormal;
                    if (swing.CritDamage * stats.LifestealPct / 100.0 >= cap) pAtCap += swing.PCrit;
                    if (swing.DoubleDamage * stats.LifestealPct / 100.0 >= cap) pAtCap += swing.PDouble;
                    double healPctPerSecond = healed / maxHp * 100.0 * 1000.0 / intervalMs;
                    table.AppendLine(FormattableString.Invariant(
                        $"{region} | {maxHp / 1000} | {swing.Mean / 1000:0.#} | {cap / 1000.0:0.#} | {name} | {stats.LifestealPct:0.##} | {rawHeal / 1000:0.##} | {healed / 1000:0.##} | {(pConnect > 0 ? pAtCap / pConnect : 0):P0} | {healPctPerSecond:0.###} | {monsterPerSwing / maxHp * 100:0.##}"));
                    Assert.True(healed >= 0);
                }
            }
            _out.WriteLine(table.ToString());
        }

        [Fact]
        public void Race_attack_speed_candidates()
        {
            var table = new StringBuilder();
            table.AppendLine("Vila (mastery 10, +speed) and Draugr (-speed): 15 points since 2026-10-29, 0.15 before. Seconds per kill of the region's third monster.");
            table.AppendLine("region | race | variant | attack speed % | interval ms | DPS | s/kill");

            var cases = new (string Race, byte Id, int Mastery, float Current, float Candidate)[]
            {
                ("Vila", RaceIds.Vila, 10, 0.15f, 15f),
                ("Draugr", RaceIds.Draugr, 0, -0.15f, -15f),
            };
            for (int region = 1; region <= 5; region++)
            {
                foreach (var c in cases)
                {
                    var payload = Reference(region, c.Id, c.Mastery);
                    var computed = SimulationEngine.LiveCombatStats(in payload);
                    var monster = ContentRegistry.Monsters[MonsterFor(region) - 1];
                    long raw = RawAttack(ref payload, in computed);
                    long monsterHp = ContentRegistry.GetScaledMonsterMaxHp(monster.Id) * 1000L;

                    foreach (var (name, delta) in new[] { ("before fix", c.Current - c.Candidate), ("current", 0f) })
                    {
                        var stats = computed;
                        stats.AttackSpeedPct += delta;
                        int interval = SimulationEngine.LiveAttackIntervalMs(in payload, in stats);
                        var swing = HuntingProjection.PlayerSwing.For(in payload, in stats, in monster, raw);
                        double dps = swing.Mean / 1000.0 * 1000.0 / interval;
                        table.AppendLine(FormattableString.Invariant(
                            $"{region} | {c.Race} | {name} | {stats.AttackSpeedPct:0.##} | {interval} | {dps:0.#} | {(dps > 0 ? monsterHp / 1000.0 / dps : double.PositiveInfinity):0.##}"));
                        Assert.True(interval > 0);
                    }
                }
            }
            _out.WriteLine(table.ToString());
        }

        [Fact]
        public void Wall_gear_dodge_before_and_after_the_curve_change()
        {
            var table = new StringBuilder();
            table.AppendLine("Each region's boss-wall gear (BossFirstClearRules) and the best gear: dodge points, monster hit chance on the old curve (no -5%) and the current one.");
            table.AppendLine("region | gear | dodge | old hit | current hit | damage taken vs old");
            for (int region = 1; region <= 5; region++)
            {
                foreach (var (name, quality, rarity) in new[]
                {
                    ("wall", BossFirstClearRules.RequiredQualityTierFor(region), BossFirstClearRules.RequiredAffixRarityFor(region)),
                    ("best", RarityTier.Transcendent, AffixRarity.Legendary),
                })
                {
                    var totals = BossGearBenchmark.BuildEquippedTotals(new ReferenceLoadout(BossGearBenchmark.ReferenceLevelForRegion(region), region, quality, rarity));
                    var stats = StatsCalculator.Calculate(0, 0, 0, 0, equippedAffixTotals: totals);
                    float oldHit = Math.Clamp(100f / (100f + stats.DodgeChancePct), 0.05f, 0.95f);
                    float newHit = SimulationEngine.MonsterHitChance(in stats);
                    table.AppendLine(FormattableString.Invariant(
                        $"{region} | {name} | {stats.DodgeChancePct:0.#} | {oldHit:P1} | {newHit:P1} | {newHit / oldHit - 1:+0.0%;-0.0%;0%}"));
                }
            }
            _out.WriteLine(table.ToString());
        }

        [Fact]
        public void Defensive_potion_dodge_candidates()
        {
            var table = new StringBuilder();
            table.AppendLine("Defensive potion of tier T: T x 0.01 dodge before 2026-10-29, T x 1 since (and the hit curve lost its 5.26-point dead zone). Monster hit chance and expected damage per monster swing.");
            table.AppendLine("tier | variant | dodge pts added | monster hit chance | damage/swing vs no potion dodge");

            var payload = Reference(3, RaceIds.Human, 0);
            var computed = SimulationEngine.LiveCombatStats(in payload);
            long maxHp = SimulationEngine.EffectiveMaxMilliHpFor(in payload, in computed);
            int monsterId = MonsterFor(3);
            double baseline = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in computed, monsterId, 0, maxHp);
            for (int tier = 1; tier <= 5; tier++)
            {
                foreach (var (name, perTier) in new[] { ("old potion", 0.01f), ("current", StatsCalculator.DefensivePotionDodgePerTier) })
                {
                    var stats = computed;
                    stats.DodgeChancePct += tier * perTier;
                    double expected = SimulationEngine.ExpectedMonsterMilliDamagePerSwing(in stats, monsterId, 0, maxHp);
                    table.AppendLine(FormattableString.Invariant(
                        $"{tier} | {name} | {tier * perTier:0.##} | {SimulationEngine.MonsterHitChance(in stats):P2} | {expected / baseline - 1:+0.00%;-0.00%;0%}"));
                    Assert.True(expected <= baseline + 1e-6);
                }
            }
            _out.WriteLine(table.ToString());
        }
    }
}
