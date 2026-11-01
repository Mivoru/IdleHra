using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The inheritance milestones: an extra effect at levels 5 and 10 of each
    /// stat (Option B, owner I1, 2026-10-31; the list approved 2026-11-01).
    /// </summary>
    /// <remarks>
    /// Modul: EVERY MILESTONE RIDES A LEVER THAT ALREADY EXISTS. Twelve new
    /// mechanics would each have needed their own offline parity; instead each
    /// effect is one more term in a composition the live tick and the away
    /// projection already share - the combat stats (LiveCombatStats), the food
    /// heal both paths read, the gathering speed and yield compositions, the
    /// loot elevation - or it pays outside the tick altogether (dungeon income,
    /// the Delve's weekly ceiling, chest opening). Only "away XP" is one-sided,
    /// and that is the point of it.
    ///
    /// Two effects of the proposal changed while building, both to an existing
    /// lever: Max health 10 was a free Death Ward each hour, which needs a
    /// persisted cooldown and a new branch in both fight loops - it is +10%
    /// armour instead. Loot luck 5 was "roll the rarity twice", which is the
    /// elevation the War tree's Rarity bough already pays - it is +5% elevation.
    ///
    /// The client mirrors <see cref="Describe"/> word for word
    /// (INHERITANCE_STATS in commands.ts, serverMirrors.test.ts).
    /// </remarks>
    public static class InheritanceMilestones
    {
        public const int FirstLevel = 5;
        public const int SecondLevel = 10;

        public const int CritChancePct = 5;
        public const int CritDamagePct = 25;
        public const int FoodHealPct = 15;
        public const int ArmourPct = 10;
        public const int AwayXpPct = 5;
        public const int GateXpPct = 25;
        public const int GateGoldPct = 25;
        public const int DelveDiamonds = 20;
        public const int GatherSpeedPct = 10;
        public const int RareFindMultiplier = 2;
        public const int ElevationPct = 5;
        public const int ChestUpgradePct = 10;

        public static bool Reached(int level, int milestoneLevel) => level >= milestoneLevel;

        /// <summary>The player-facing line for a stat's milestone at level 5 (<paramref name="second"/> false) or 10.</summary>
        public static string Describe(int statId, bool second) => (statId, second) switch
        {
            (InheritanceRegistry.StatDamage, false) => "+5% critical chance",
            (InheritanceRegistry.StatDamage, true) => "+25% critical damage",
            (InheritanceRegistry.StatMaxHp, false) => "Food heals 15% more",
            (InheritanceRegistry.StatMaxHp, true) => "+10% armour",
            (InheritanceRegistry.StatXpGain, false) => "+5% XP while away",
            (InheritanceRegistry.StatXpGain, true) => "+25% XP from Koschei's gate",
            (InheritanceRegistry.StatGoldGain, false) => "+25% gold from Zmey's gate",
            (InheritanceRegistry.StatGoldGain, true) => "+20 Delve diamonds a week",
            (InheritanceRegistry.StatGatheringYield, false) => "+10% gathering speed",
            (InheritanceRegistry.StatGatheringYield, true) => "Rare finds twice as likely",
            (InheritanceRegistry.StatLootLuck, false) => "+5% chance a drop is one rarity higher",
            (InheritanceRegistry.StatLootLuck, true) => "10% chance a chest opens one tier higher",
            _ => string.Empty,
        };

        /// <summary>Damage 5 and 10, Max health 10: the combat stats both fight paths read (SimulationEngine.LiveCombatStats).</summary>
        public static void ApplyCombatStats(ref CombatStats stats, in TickStatePayload payload)
        {
            if (Reached(payload.Inherit_Damage, FirstLevel)) stats.CritChancePct += CritChancePct;
            if (Reached(payload.Inherit_Damage, SecondLevel)) stats.EquipmentCritDamagePct += CritDamagePct;
            if (Reached(payload.Inherit_MaxHp, SecondLevel) && stats.FlatPhysicalArmor > 0)
                stats.FlatPhysicalArmor += stats.FlatPhysicalArmor * ArmourPct / 100;
        }

        /// <summary>Max health 5: one bite's heal, for the live auto-eat and the away projection alike.</summary>
        public static int FoodHealMilliHp(in TickStatePayload payload, int foodItemId, long maxMilliHp)
        {
            int heal = FoodRegistry.GetHealMilliHp(foodItemId, maxMilliHp);
            if (!Reached(payload.Inherit_MaxHp, FirstLevel) || heal <= 0) return heal;
            return (int)Math.Min(int.MaxValue, (long)heal * (100 + FoodHealPct) / 100);
        }

        /// <summary>Experience 5: combat XP earned while away.</summary>
        public static long AwayXp(in TickStatePayload payload, long xp)
            => Reached(payload.Inherit_XpGain, FirstLevel) && xp > 0 ? xp + xp * AwayXpPct / 100 : xp;

        /// <summary>Experience 10 and Gold 5: the gate's hourly income a run pays minutes of.</summary>
        public static (long HourlyXp, long HourlyGold) DungeonIncome(in TickStatePayload payload, long hourlyXp, long hourlyGold)
        {
            if (Reached(payload.Inherit_XpGain, SecondLevel)) hourlyXp += hourlyXp * GateXpPct / 100;
            if (Reached(payload.Inherit_GoldGain, FirstLevel)) hourlyGold += hourlyGold * GateGoldPct / 100;
            return (hourlyXp, hourlyGold);
        }

        /// <summary>Gold 10: the Delve's weekly diamond ceiling for a player with this Gold level.</summary>
        public static int DelveDiamondCeiling(int goldLevel)
            => DelveRegistry.MaxDiamondsPerWeek + (Reached(goldLevel, SecondLevel) ? DelveDiamonds : 0);

        /// <summary>Gathering 5: whole percent of gathering speed.</summary>
        public static int GatherSpeedBonusPct(in TickStatePayload payload)
            => Reached(payload.Inherit_GatheringYield, FirstLevel) ? GatherSpeedPct : 0;

        /// <summary>Gathering 10: the rare-favouring weight bonus, doubled.</summary>
        public static int RareFindWeight(in TickStatePayload payload, int luckWeightBonus)
            => Reached(payload.Inherit_GatheringYield, SecondLevel) ? luckWeightBonus * RareFindMultiplier : luckWeightBonus;

        /// <summary>Loot luck 5: percent added to the chance a drop rolls one rarity higher.</summary>
        public static float ElevationPctFor(int lootLuckLevel)
            => Reached(lootLuckLevel, FirstLevel) ? ElevationPct : 0f;

        /// <summary>Loot luck 10: whether one chest opens a tier higher (never above Ancient).</summary>
        public static bool UpgradesChest(int lootLuckLevel, Random rng)
            => Reached(lootLuckLevel, SecondLevel) && rng.Next(100) < ChestUpgradePct;
    }
}
