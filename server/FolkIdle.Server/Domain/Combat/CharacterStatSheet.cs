using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using static System.FormattableString;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>One line of the sheet. Value is in the row's Unit; Cap is null when nothing bounds it.</summary>
    public sealed class StatSheetRow
    {
        public string Key { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public double Value { get; set; }
        /// <summary>"flat", "pct", "mult", "per_s" or "s".</summary>
        public string Unit { get; set; } = "flat";
        public double? Cap { get; set; }
        /// <summary>True when more of this stat buys nothing.</summary>
        public bool AtCap { get; set; }
        /// <summary>What the number means, or where its ceiling sits, in words.</summary>
        public string? Note { get; set; }
    }

    public sealed class StatSheetSection
    {
        public string Title { get; set; } = string.Empty;
        public List<StatSheetRow> Rows { get; set; } = new();
    }

    public sealed class StatSheetResponse
    {
        public int Slot { get; set; }
        public List<StatSheetSection> Sections { get; set; } = new();
    }

    /// <summary>
    /// Every stat of the character in the active register, with the ceiling
    /// each one runs into (owner, 2026-10-10: "all the stats, with a cap where
    /// there is one, so a player knows to stop buying attack speed").
    ///
    /// Modul: NOTHING HERE IS A FORMULA OF ITS OWN. Every number is asked of
    /// the function the tick uses - LiveCombatStats, EffectiveMilliAttackFor,
    /// LiveAttackIntervalMs, LiveCritChancePct, LiveCritMultiplier,
    /// EffectiveMaxMilliHpFor, MonsterHitChance, PlayerBlockFraction,
    /// LiveKillXpMultiplierPct, CombatGoldReward, GatherSpeedTermsFor and
    /// GatheringYieldFor - and every cap is the constant the tick clamps on. A
    /// sheet that recomputed them would be a second damage model, which is the
    /// thing this codebase keeps deleting. The client only formats the answer.
    ///
    /// Call it on a COPY of the payload with the slot already swapped into the
    /// active register (SimulationEngine.SwapSlotIntoActiveRegister).
    /// </summary>
    public static class CharacterStatSheet
    {
        public static StatSheetResponse Build(TickStatePayload payload, int slot)
        {
            CombatStats stats = SimulationEngine.LiveCombatStats(in payload);
            long maxMilliHp = SimulationEngine.EffectiveMaxMilliHpFor(in payload, in stats);
            long milliAttack = SimulationEngine.EffectiveMilliAttackFor(
                ref payload, in stats, SimulationEngine.LineageOf(in payload).DamageScalePerLevelPct);
            int intervalMs = SimulationEngine.LiveAttackIntervalMs(in payload, in stats);
            float maxSpeedPct = CombatDamageModel.MaxAttackSpeedReduction * 100f;

            var offence = new StatSheetSection { Title = "Offence" };
            offence.Rows.Add(new StatSheetRow
            {
                Key = "attack", Label = "Attack", Value = Math.Round(milliAttack / 1000.0, 1),
                Note = "One hit before crits, the monster's armour and the codex. Level, gear, damage bonuses, inheritance, guild and Renown are all in it.",
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "damage_pct", Label = "Damage bonus", Value = Math.Round(stats.EquipmentDamagePct, 1), Unit = "pct",
                Note = "Melee, ranged and magic damage affixes, pets and Might milestones. Multiplies attack. No cap.",
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "codex_damage", Label = "Codex damage", Value = Math.Round(payload.CachedCodexDamageMultiplier, 2), Unit = "mult",
                Note = "From monster codex levels, on every hit. Grows ever more slowly (square root), never stops.",
            });
            int renown = RebirthRules.DamageBonusPct(payload.RenownedRebirths);
            offence.Rows.Add(new StatSheetRow
            {
                Key = "renown", Label = "Renown", Value = renown, Unit = "pct", Cap = RebirthRules.MaxDamageBonusPct,
                AtCap = renown >= RebirthRules.MaxDamageBonusPct - 1,
                Note = Invariant($"Damage from rebirths taken at level {RebirthRules.RenownLevel}+. Approaches {RebirthRules.MaxDamageBonusPct}% and never reaches it."),
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "attack_speed", Label = "Attack speed", Value = Math.Round(stats.AttackSpeedPct, 1), Unit = "pct", Cap = maxSpeedPct,
                AtCap = stats.AttackSpeedPct >= maxSpeedPct,
                Note = Invariant($"Shortens the {1500 / 1000.0:0.#} s swing. Anything above {maxSpeedPct:0}% does nothing."),
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "swing", Label = "Time per swing", Value = Math.Round(intervalMs / 1000.0, 2), Unit = "s",
                Note = "After attack speed and Relentless. Never below 0.2 s.",
                AtCap = intervalMs <= 200,
            });
            float crit = SimulationEngine.LiveCritChancePct(in payload, in stats);
            offence.Rows.Add(new StatSheetRow
            {
                Key = "crit_chance", Label = "Crit chance", Value = Math.Round(crit, 1), Unit = "pct", Cap = SimulationEngine.MaxCritChancePct,
                AtCap = crit >= SimulationEngine.MaxCritChancePct,
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "crit_damage", Label = "Crit damage", Value = Math.Round(SimulationEngine.LiveCritMultiplier(in payload, in stats), 2), Unit = "mult",
                Note = "What a crit multiplies the hit by. No cap.",
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "accuracy", Label = "Accuracy", Value = stats.AccuracyRating,
                Note = "Against the monster's dodge: hit chance = (100 + accuracy) / (100 + dodge), never above 95%.",
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "armour_pen", Label = "Armour penetration", Value = stats.FlatArmorPenetration,
                Note = "Ignores this much of the monster's armour.",
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "lifesteal", Label = "Lifesteal", Value = Math.Round(stats.LifestealPct, 1), Unit = "pct",
                Note = Invariant($"Of each hit's damage, healed - at most 1% of your health ({maxMilliHp / 100_000.0:0.#}) per hit."),
            });
            offence.Rows.Add(new StatSheetRow
            {
                Key = "world_boss", Label = "World boss damage", Value = payload.CachedAffixTotals.WorldBossDamageTenthsPct / 10.0, Unit = "pct",
            });

            var defence = new StatSheetSection { Title = "Defence" };
            defence.Rows.Add(new StatSheetRow { Key = "max_hp", Label = "Health", Value = maxMilliHp / 1000 });
            defence.Rows.Add(new StatSheetRow
            {
                Key = "armour", Label = "Armour", Value = stats.FlatPhysicalArmor,
                Note = "Shrinks every monster hit; each region needs about three times more for the same effect.",
            });
            float blockCap = 75f;
            float blockPct = SimulationEngine.PlayerBlockFraction(in stats) * 100f;
            defence.Rows.Add(new StatSheetRow
            {
                Key = "block", Label = "Block", Value = Math.Round(stats.BlockStrengthPct, 1), Unit = "pct", Cap = blockCap,
                AtCap = blockPct >= blockCap,
                Note = Invariant($"The share of each landed hit taken off after armour. Anything above {blockCap:0}% does nothing."),
            });
            float monsterHit = SimulationEngine.MonsterHitChance(in stats) * 100f;
            defence.Rows.Add(new StatSheetRow
            {
                Key = "dodge", Label = "Dodge", Value = Math.Round(stats.DodgeChancePct, 1),
                Note = Invariant($"Monsters land {monsterHit:0.#}% of their swings on you: 100 / (100 + dodge), between 5% and 95% - so the first 5 points change nothing."),
                AtCap = monsterHit <= 5f,
            });
            defence.Rows.Add(new StatSheetRow
            {
                Key = "regen", Label = "Health regeneration", Value = Math.Round(stats.OutOfCombatHpRegen, 1), Unit = "per_s",
                Note = "Between fights.",
            });
            float critMitigation = stats.CritMitigationPct;
            defence.Rows.Add(new StatSheetRow
            {
                Key = "crit_mitigation", Label = "Crit protection", Value = Math.Round(critMitigation, 1), Unit = "pct", Cap = 50,
                AtCap = critMitigation >= 50f,
                Note = "Takes off a monster crit's 1.5x. At 50% a crit hits like a normal blow; more does nothing.",
            });

            var rewards = new StatSheetSection { Title = "Rewards" };
            rewards.Rows.Add(new StatSheetRow
            {
                Key = "xp", Label = "Combat XP bonus", Value = SimulationEngine.LiveKillXpMultiplierPct(in payload, 100) - 100, Unit = "pct",
                Note = "Your own bonus, before a server-wide event.",
            });
            rewards.Rows.Add(new StatSheetRow
            {
                Key = "gold", Label = "Gold bonus", Value = Math.Round(CombatGoldReward.FactorsFor(in payload, stats.GoldAcquisitionMultiplierPct).TotalBonusPct, 1), Unit = "pct",
                Note = "On gold from kills: race, Fortune, inheritance, guild, legacy and pet together.",
            });
            rewards.Rows.Add(new StatSheetRow
            {
                Key = "loot_luck", Label = "Loot luck", Value = Math.Round(stats.LootLuckPct, 1), Unit = "pct",
                Note = "Makes rarer drops likelier. It does not make drops more frequent. Grows ever more slowly, never stops.",
            });
            rewards.Rows.Add(new StatSheetRow
            {
                Key = "elevation", Label = "Rarity elevation", Value = Math.Round(stats.RarityElevationPct, 1), Unit = "pct",
                Note = "The chance a dropped piece comes out one rarity tier higher.",
            });

            var gathering = new StatSheetSection { Title = "Gathering" };
            for (int profession = 0; profession <= 2; profession++)
            {
                AddGatheringRows(gathering, ref payload, profession);
            }
            gathering.Rows.Add(new StatSheetRow
            {
                Key = "codex_yield", Label = "Codex yield", Value = Math.Round(payload.CachedCodexYieldMultiplier, 2), Unit = "mult",
                Cap = CodexEngine.MaxYieldMultiplier, AtCap = payload.CachedCodexYieldMultiplier >= CodexEngine.MaxYieldMultiplier,
                Note = "Multiplies every harvest's rolls.",
            });

            return new StatSheetResponse { Slot = slot, Sections = { offence, defence, rewards, gathering } };
        }

        private static readonly string[] ProfessionNames = { "Woodcutting", "Mining", "Fishing" };

        private static void AddGatheringRows(StatSheetSection section, ref TickStatePayload payload, int profession)
        {
            string name = ProfessionNames[profession];
            var terms = SimulationEngine.GatherSpeedTermsFor(ref payload, profession);
            var row = new StatSheetRow
            {
                Key = $"gather_speed_{profession}", Label = $"{name} speed", Value = terms.TotalPct, Unit = "pct",
                Note = $"Tool +{GatheringToolEngine.GetToolSpeedBonusPct(terms.ToolTier)}%, mastery {terms.MasteryLevel} +{GatheringToolEngine.GetMasterySpeedBonusPct(terms.MasteryLevel)}%"
                    + (terms.VillageLevel > 0 ? $", village +{terms.VillageLevel * GatheringToolEngine.VillageYieldBonusPctPerLevel}%" : string.Empty)
                    + (terms.ExtraPct > 0 ? $", affixes, tree, bloodline and pet +{terms.ExtraPct}%" : string.Empty)
                    + ". A harvest never takes under 0.2 s.",
            };

            // The character's own node, when it works this profession: the
            // seconds per harvest, and whether the 0.2 s floor already binds.
            if (ContentRegistry.TryGetGatheringNode(payload.ActiveActivityId, out var current) && current.ProfessionType == profession)
            {
                int ticks = SimulationEngine.RequiredGatherTicks(ref payload, in current);
                row.AtCap = ticks <= GatheringToolEngine.MinRequiredTicks;
                row.Note += Invariant($" Here: {ticks * SimulationEngine.TickDurationMs / 1000.0:0.0#} s a harvest") + (row.AtCap ? " - at the floor, more speed does nothing on this node." : ".");
            }
            section.Rows.Add(row);

            if (TryFirstNode(profession, out var node))
            {
                var yield = SimulationEngine.GatheringYieldFor(ref payload, in node, 100);
                section.Rows.Add(new StatSheetRow
                {
                    Key = $"gather_yield_{profession}", Label = $"{name} yield", Value = Math.Round(yield.MultiplierPct / 100.0, 2), Unit = "mult",
                    Note = "Loot rolls per harvest, with codex yield.",
                });
            }
        }

        private static bool TryFirstNode(int profession, out GatheringNodeDefinition node)
        {
            foreach (var candidate in ContentRegistry.GatheringNodes)
            {
                if (candidate.ProfessionType == profession)
                {
                    node = candidate;
                    return true;
                }
            }
            node = default;
            return false;
        }
    }
}
