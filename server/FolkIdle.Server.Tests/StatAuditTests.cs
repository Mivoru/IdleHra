using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using Xunit;
using Xunit.Abstractions;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// THE STAT AUDIT (TASK_BOARD 126). Nearly every defect found on 2026-10-29
    /// had one shape: something a player buys, rolls, breeds or is shown, that
    /// the simulation never reads, or reads in the wrong unit. Examples:
    /// inheritance Gathering yield, the Fortitude armour, the tool yield and
    /// rare find, Draugr's lifesteal divided by 100 twice, the 0.15 attack
    /// speeds, the dodge dead zone, and gather_speed_pct paying 10x its
    /// tooltip. Each was found by reading code, after it had shipped. This
    /// finds the next one mechanically.
    ///
    /// A PROBE is the set of figures the tick actually fights and gathers with,
    /// each read through the SAME function the tick calls: attack, health,
    /// armour, crit, swing interval, monster hit chance, lifesteal, block,
    /// regen, loot luck, gold and XP per kill, and per profession the yield,
    /// the rare-favouring luck weight and the harvest time.
    ///
    /// The rule: every registry entry moves the probe when set from zero to
    /// its maximum. An entry whose consumer the probe cannot see (a boss-only
    /// bonus, an offline cap) is allowed only through NotInProbe, which names
    /// the consumer. A new stat with no consumer has no line there, and fails.
    /// </summary>
    [Collection("Static loot queues")]
    public class StatAuditTests
    {
        private readonly ITestOutputHelper _out;

        public StatAuditTests(ITestOutputHelper output)
        {
            _out = output;
            ContentRegistry.Initialize();
        }

        // ------------------------------------------------------------------
        // The probe
        // ------------------------------------------------------------------

        private static TickStatePayload Baseline() => new TickStatePayload
        {
            CurrentLevel = 40, STR = 60, DEX = 60, CON = 60, LCK = 30,
            CachedCodexYieldMultiplier = 1f, CachedCodexDamageMultiplier = 1f,
        };

        private static TickStatePayload WithRace(byte raceId, int mastery)
        {
            var p = Baseline();
            p.Slot1_CharacterId = Guid.NewGuid();
            p.Slot1_AgePhase = 1;
            p.Slot1_GeneticVector = raceId;
            switch (raceId)
            {
                case RaceIds.Human: p.HumanMasteryLevel = mastery; break;
                case RaceIds.Vila: p.VilaMasteryLevel = mastery; break;
                case RaceIds.Draugr: p.DraugrMasteryLevel = mastery; break;
                case RaceIds.Kobold: p.KoboldMasteryLevel = mastery; break;
                case RaceIds.Vodnik: p.VodnikMasteryLevel = mastery; break;
                case RaceIds.Moosleute: p.MoosleuteMasteryLevel = mastery; break;
            }
            return p;
        }

        // The SLOWEST node of each profession: on a quick one a +5% speed bonus
        // rounds away inside ComputeRequiredTicks and reads as "no effect".
        private static GatheringNodeDefinition NodeOf(int profession)
            => ContentRegistry.GatheringNodes.ToArray().Where(n => n.ProfessionType == profession)
                .OrderByDescending(n => n.BaseTickThreshold).First();

        internal static Dictionary<string, double> Probe(TickStatePayload payload)
        {
            var stats = SimulationEngine.LiveCombatStats(in payload);
            long maxHp = SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            // Monster 111 (region 5, 2,700 base gold): on the first monster's 23
            // gold a +5% bonus rounds away and reads as "no effect".
            var monster = ContentRegistry.Monsters[110];
            var probe = new Dictionary<string, double>
            {
                ["attack"] = SimulationEngine.EffectiveMilliAttackFor(ref payload, in stats, SimulationEngine.LineageOf(in payload).DamageScalePerLevelPct),
                ["max_hp"] = maxHp,
                ["armour"] = stats.FlatPhysicalArmor,
                ["armour_pen"] = stats.FlatArmorPenetration,
                ["crit_chance"] = SimulationEngine.LiveCritChancePct(in payload, in stats),
                ["crit_mult"] = SimulationEngine.LiveCritMultiplier(in payload, in stats),
                ["interval_ms"] = SimulationEngine.LiveAttackIntervalMs(in payload, in stats),
                ["monster_hit"] = SimulationEngine.MonsterHitChance(in stats),
                ["monster_crit_mult"] = SimulationEngine.MonsterCritMultiplier(in stats),
                ["lifesteal"] = stats.LifestealPct,
                ["block"] = stats.BlockStrengthPct,
                ["regen"] = stats.OutOfCombatHpRegen,
                ["loot_luck"] = stats.LootLuckPct,
                ["gold_per_kill"] = CombatGoldReward.PerKill(in payload, in monster, stats.GoldAcquisitionMultiplierPct),
                ["xp_mult"] = SimulationEngine.LiveKillXpMultiplierPct(in payload, 100),
            };
            for (int profession = 0; profession <= 2; profession++)
            {
                var node = NodeOf(profession);
                var yield = SimulationEngine.GatheringYieldFor(ref payload, in node, 100);
                probe[$"yield_{profession}"] = yield.MultiplierPct;
                probe[$"luck_weight_{profession}"] = yield.LuckWeightBonus;
                probe[$"gather_ticks_{profession}"] = SimulationEngine.RequiredGatherTicks(ref payload, in node);
            }
            return probe;
        }

        private static List<string> Moved(Dictionary<string, double> before, Dictionary<string, double> after)
            => before.Keys.Where(k => Math.Abs(before[k] - after[k]) > 1e-9).ToList();

        // ------------------------------------------------------------------
        // The allow-list: entries whose consumer is real but outside the probe
        // ------------------------------------------------------------------

        // Key: "<registry>:<id>". Value: where it is read, so a reviewer can
        // check the claim. An entry here is a promise that the consumer
        // exists; when one is found not to, it is a defect, not a list edit.
        private static readonly Dictionary<string, string> NotInProbe = new()
        {
            ["inheritance:LootLuck"] = "CombatLootEngine's drop roll (Inheritance term), not CombatStats.LootLuckPct",
            // The War half's nodes the probe cannot see; each is proven where it
            // lands by WarMechanicParityTests.
            ["war:scavenger"] = "loot luck, LootLuckBreakdown (WarMechanicParityTests.Fortune_loot_nodes_reach_the_drop_request)",
            ["war:trophy_hunter"] = "region boss gold, CombatGoldReward boss path",
            ["war:plenty"] = "material quantity per drop, CombatLootDropRequest",
            ["war:rarity"] = "rarity elevation chance, LootLuckBreakdown",
            ["war:codex_scholar"] = "the codex damage bonus, which the probe holds at x1 (WarMechanicParityTests)",
            ["war:golden_fleece"] = "every Nth kill, ApplyKillProgression",
            ["war:breach"] = "First Strike - a monster at full health, RunCombatTick / FightSetup",
            ["war:crushing_blow"] = "a hit doubled, RunCombatTick / HuntingProjection.Landed",
            ["war:executioner"] = "a monster below 35%, RunCombatTick / FightSetup",
            ["war:bossbreaker"] = "first-clear boss HP, BossFirstClearRules.ReliefFor",
            ["war:thunderer"] = "opening blow on a boss, PendingSkillDamageMultiplier",
            ["war:echo"] = "a crit repeats, RunCombatTick / PlayerSwing",
            ["war:steady_hand"] = "accuracy, which the probe does not read (CombatDamageModel.HitChance)",
            ["war:double_strike"] = "a crit lands twice, RunCombatTick / PlayerSwing",
            ["war:perfect_sequence"] = "every Nth swing a crit, RunCombatTick / PlayerSwing",
            ["war:bloodthirst"] = "damage dealt as healing, RunCombatTick / HuntingProjection",
            ["war:riposte"] = "a dodged or blocked swing answers, RunCombatTick / PlayerSwing",
            ["war:last_stand"] = "survives one killing blow an hour, ApplyCombatDeath",
            ["war:know_your_prey"] = "damage and gold on the frontier region, RunCombatTick / Landed / CombatGoldReward",
            ["trait:fae_touched"] = "rarity elevation, CombatLootEngine",
            ["pet:WorldBossDamage"] = "world boss strike, ServerStrikeDamage",
            ["pet:DropChance"] = "drop chance, CombatLootEngine",
            ["race:5:mastery"] = "Vodyanoy mastery - offline cap, OfflineSimulationEngine.EffectiveOfflineCapSeconds",
            // The Hearth half's wired nodes the probe cannot see (it reads
            // professions 0-2 and no crafting or reward-rate stream).
            ["hearth:green_thumb"] = "herbalism speed, GatherSpeedTermsFor profession 3 (HearthTreeRegistryTests)",
            ["hearth:thrifty"] = "the keep-materials chance, CraftingEngine.CraftIsFree",
            ["hearth:homebody"] = "the reward rate, ScholarRate (OfflineLootParityTests)",
            ["hearth:long_absence"] = "the reward rate, ScholarRate (OfflineLootParityTests)",
            ["hearth:deep_vein"] = "every 20th harvest of wood or ore, RunGatheringTick / CalculateGatheringProjection (HearthTreeRegistryTests)",
            ["hearth:moonlit_catch"] = "every 20th catch of fish or herbs, RunGatheringTick / CalculateGatheringProjection (HearthTreeRegistryTests)",
        };

        // KNOWN DEFECTS: entries that SHOULD move the probe and do not, each
        // with where it is tracked. The test passes while they stay broken and
        // FAILS the moment one starts working, so the fix also deletes its row
        // - a list of known defects that nobody prunes is how this codebase
        // lost track of the last batch.
        private static readonly Dictionary<string, string> KnownDefects = new()
        {
        };

        private void AssertMovesOrIsListed(string key, Dictionary<string, double> before, Dictionary<string, double> after, List<string> failures)
        {
            var moved = Moved(before, after);
            bool listed = NotInProbe.ContainsKey(key);
            bool known = KnownDefects.ContainsKey(key);
            string outcome = moved.Count > 0 ? string.Join(", ", moved)
                : listed ? "(outside the probe: " + NotInProbe[key] + ")"
                : known ? "KNOWN DEFECT: " + KnownDefects[key]
                : "NOTHING";
            _out.WriteLine($"{key,-34} {outcome}");
            if (moved.Count == 0 && !listed && !known)
                failures.Add($"{key} changes nothing the tick uses, and is not in NotInProbe");
            if (moved.Count > 0 && known)
                failures.Add($"{key} works now ({string.Join(", ", moved)}) - delete its KnownDefects row");
        }

        // ------------------------------------------------------------------
        // 1. Every registry entry has a consumer
        // ------------------------------------------------------------------

        [Fact]
        public void Every_inheritance_stat_moves_the_probe()
        {
            var failures = new List<string>();
            var before = Probe(Baseline());
            for (int stat = 0; stat < InheritanceRegistry.StatCount; stat++)
            {
                var p = Baseline();
                SimulationEngine.SetInheritanceLevel(ref p, stat, (byte)InheritanceRegistry.MaxLevel);
                string name = stat switch
                {
                    InheritanceRegistry.StatDamage => "Damage", InheritanceRegistry.StatMaxHp => "MaxHp",
                    InheritanceRegistry.StatXpGain => "XpGain", InheritanceRegistry.StatGoldGain => "GoldGain",
                    InheritanceRegistry.StatGatheringYield => "GatheringYield", InheritanceRegistry.StatLootLuck => "LootLuck",
                    _ => stat.ToString(),
                };
                AssertMovesOrIsListed($"inheritance:{name}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_skill_tree_node_moves_the_probe()
        {
            // The War half since rework slice 2.5; every node at its maximum.
            var failures = new List<string>();
            var before = Probe(Baseline());
            foreach (var node in WarTreeRegistry.All())
            {
                var p = TestTree.With(Baseline(), (node.Id, (byte)node.MaxLevel));
                AssertMovesOrIsListed($"war:{node.Icon}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_buyable_hearth_node_moves_the_probe()
        {
            // The Hearth half (step 4.3). Pending nodes cannot be bought, so
            // they are skipped here and refused by IsPending.
            var failures = new List<string>();
            var before = Probe(Baseline());
            foreach (var node in HearthTreeRegistry.All())
            {
                if (HearthTreeRegistry.IsPending(node.Id)) continue;
                var p = Baseline();
                p.SkillRanks[node.Id] = (byte)node.MaxLevel;
                AssertMovesOrIsListed($"hearth:{node.Icon}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_heritable_trait_moves_the_probe()
        {
            var failures = new List<string>();
            var before = Probe(Baseline());
            foreach (var trait in TraitRegistry.All)
            {
                var p = Baseline();
                p.TraitMask = 1L << trait.Bit;
                AssertMovesOrIsListed($"trait:{trait.Key}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_pet_stat_moves_the_probe()
        {
            var failures = new List<string>();
            var before = Probe(Baseline());
            foreach (PetStat stat in Enum.GetValues<PetStat>())
            {
                var pet = PetRegistry.All.FirstOrDefault(p => p.Bonuses.Any(b => b.Stat == stat));
                if (pet == null) { failures.Add($"pet:{stat} exists in PetStat but no pet carries it"); continue; }
                var p = Baseline();
                PetRegistry.AddTo(pet, ref p.CachedAffixTotals);
                AssertMovesOrIsListed($"pet:{stat}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_affix_moves_the_probe()
        {
            var failures = new List<string>();
            var before = Probe(Baseline());
            foreach (var definition in AffixRegistry.Definitions)
            {
                var p = WithAffix(Baseline(), definition.Id, 200);
                AssertMovesOrIsListed($"affix:{definition.Id}", before, Probe(p), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        [Fact]
        public void Every_race_innate_and_mastery_moves_the_probe()
        {
            var failures = new List<string>();
            var raceless = Probe(Baseline());
            foreach (byte race in new[] { RaceIds.Human, RaceIds.Vila, RaceIds.Draugr, RaceIds.Kobold, RaceIds.Vodnik, RaceIds.Moosleute })
            {
                var innate = Probe(WithRace(race, 0));
                AssertMovesOrIsListed($"race:{race}:innate", raceless, innate, failures);
                AssertMovesOrIsListed($"race:{race}:mastery", innate, Probe(WithRace(race, 50)), failures);
            }
            Assert.True(failures.Count == 0, string.Join("\n", failures));
        }

        /// <summary>
        /// An affix on the slot that rolls it: combat affixes through
        /// EquipmentSlotEngine.AddAffixTotals (the equip path's own fold), tool
        /// affixes onto the payload fields ToolLoadoutResolver fills.
        /// </summary>
        private static TickStatePayload WithAffix(TickStatePayload p, string id, int magnitude)
        {
            switch (id)
            {
                case ToolLoadoutResolver.GatherSpeedAffix:
                    p.ToolAxeSpeedPct = p.ToolPickaxeSpeedPct = p.ToolRodSpeedPct = (ushort)magnitude; return p;
                case ToolLoadoutResolver.GatherYieldAffix:
                    p.ToolAxeYieldPct = p.ToolPickaxeYieldPct = p.ToolRodYieldPct = (ushort)magnitude; return p;
                case ToolLoadoutResolver.RareFindAffix:
                    // Large enough that the integer luck weight visibly moves.
                    p.ToolAxeRareFindPct = p.ToolPickaxeRareFindPct = p.ToolRodRareFindPct = (ushort)(magnitude * 10); return p;
            }
            EquipmentSlotEngine.AddAffixTotals($"{{\"{id}@3\":{magnitude}}}", ref p.CachedAffixTotals);
            return p;
        }

        // ------------------------------------------------------------------
        // 2. Units: a percentage affix pays what its tooltip prints
        // ------------------------------------------------------------------

        // The tooltip prints magnitude / 10 as a percent (affixes.ts
        // formatAffixValue). Each row: the probe figure that should move by
        // exactly that many points, and its scale per point.
        private static readonly (string Affix, string Figure, double PerPoint)[] UnitRows =
        {
            ("crit_chance_pct", "crit_chance", 1.0),
            ("crit_dmg_pct", "crit_mult", 0.01),
            ("lifesteal_pct", "lifesteal", 1.0),
            ("block_chance_pct", "block", 1.0),
            ("gather_yield_pct", "yield_0", 1.0),
        };

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void A_percentage_affix_pays_exactly_what_its_tooltip_prints(int row)
        {
            var (affix, figure, perPoint) = UnitRows[row];
            const int magnitude = 50; // tooltip: +5.0%
            var before = Probe(Baseline());
            var after = Probe(WithAffix(Baseline(), affix, magnitude));
            double expected = magnitude / 10.0 * perPoint;
            Assert.Equal(expected, after[figure] - before[figure], 3);
        }

        [Fact]
        public void The_tool_speed_affix_pays_the_whole_percent_its_tooltip_now_prints()
        {
            // gather_speed_pct is the one affix shown as WHOLE percent
            // (affixes.ts, TASK_BOARD 121 / issue #203): magnitude 50 reads
            // "+50%". The tick multiplies the speed by 1 + 50/100.
            var p = WithAffix(Baseline(), "gather_speed_pct", 50);
            var bare = Baseline();
            Assert.Equal(50, SimulationEngine.GatherSpeedTermsFor(ref p, 0).ExtraPct - SimulationEngine.GatherSpeedTermsFor(ref bare, 0).ExtraPct);
            var node = NodeOf(0);
            int bareTicks = SimulationEngine.RequiredGatherTicks(ref bare, in node);
            int tooledTicks = SimulationEngine.RequiredGatherTicks(ref p, in node);
            Assert.InRange(tooledTicks, bareTicks * 100 / 150 - 1, bareTicks * 100 / 150 + 1);
        }

        // ------------------------------------------------------------------
        // 3. Parity: the stat sheet shows the figures the tick uses
        // ------------------------------------------------------------------

        [Fact]
        public void The_stat_sheet_shows_what_the_tick_fights_with()
        {
            // A loaded character: every source the sheet has a row for.
            var p = WithRace(RaceIds.Draugr, 50);
            SimulationEngine.SetSkillTreeLevel(ref p, SkillTreeRegistry.BoughFortitude, 8);
            SimulationEngine.SetSkillTreeLevel(ref p, SkillTreeRegistry.BranchCritChance, 10);
            SimulationEngine.SetInheritanceLevel(ref p, InheritanceRegistry.StatMaxHp, 10);
            SimulationEngine.SetInheritanceLevel(ref p, InheritanceRegistry.StatGatheringYield, 10);
            p = WithAffix(p, "lifesteal_pct", 30);
            p = WithAffix(p, "gather_yield_pct", 60);
            PetRegistry.AddTo(PetRegistry.All.First(x => x.Bonuses.Any(b => b.Stat == PetStat.Armour)), ref p.CachedAffixTotals);

            var probe = Probe(p);
            var rows = CharacterStatSheet.Build(p, 0).Sections.SelectMany(s => s.Rows).ToDictionary(r => r.Key);

            Assert.Equal(Math.Floor(probe["max_hp"] / 1000), rows["max_hp"].Value);
            Assert.Equal(probe["armour"], rows["armour"].Value);
            Assert.Equal(Math.Round(probe["crit_chance"], 1), rows["crit_chance"].Value, 1);
            Assert.Equal(Math.Round(probe["crit_mult"], 2), rows["crit_damage"].Value, 2);
            Assert.Equal(Math.Round(probe["lifesteal"], 1), rows["lifesteal"].Value, 1);
            for (int profession = 0; profession <= 2; profession++)
                Assert.Equal(Math.Round(probe[$"yield_{profession}"] / 100.0, 2), rows[$"gather_yield_{profession}"].Value, 2);
        }
    }
}
