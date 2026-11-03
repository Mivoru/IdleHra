using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>The five affix families (roadmap 8, phase 5.1): one per tree column that has a use for them.</summary>
    public enum AffixFamily
    {
        None = 0,
        /// <summary>Might: damage, armour penetration, crushing blow, world-boss damage.</summary>
        Power = 1,
        /// <summary>Precision: crit chance, crit damage, attack speed.</summary>
        Precision = 2,
        /// <summary>Guard: health, armour, dodge, block, lifesteal, thorns.</summary>
        Guard = 3,
        /// <summary>Fortune: loot luck and gold find.</summary>
        Fortune = 4,
        /// <summary>Hearth: the gathering tools' speed, yield and rare find.</summary>
        Craft = 5,
    }

    /// <summary>
    /// Affix families: one node per tree column makes the affixes of its family
    /// stronger by a share of themselves (+3/6/10%), so a Might player wants
    /// Power affixes and the tree says what to look for when rerolling.
    /// </summary>
    /// <remarks>
    /// Modul: ONLY THE AFFIX PART IS SCALED (phase 5.1, 2026-11-03). The cached
    /// totals also carry an item's own base attack and armour and every pet's
    /// bonuses, which a family node must not touch - "+10% to your Guard
    /// affixes" is not "+10% to your chest piece". AddAffixTotals therefore
    /// records each family field's affix-only amount in
    /// <see cref="EquippedAffixTotals.AffixPart"/>, and <see cref="Totals"/>
    /// adds the family's share of that part alone.
    ///
    /// Read at USE, not baked into the cache: the cached totals are computed at
    /// equip and login, and a respec must change the bonus at once, so every
    /// reader that used payload.CachedAffixTotals goes through Totals(). It is a
    /// handful of integer multiplies, so the per-swing simulator pays nothing
    /// measurable for it.
    /// </remarks>
    public static class AffixFamilies
    {
        public static AffixFamily FamilyOf(string affixId) => AffixRegistry.StripStackSuffix(affixId) switch
        {
            "melee_dmg_pct" or "range_dmg_pct" or "magic_dmg_pct" or "armor_pen_flat" or "crushing_blow_pct" or "boss_dmg_pct" => AffixFamily.Power,
            "crit_chance_pct" or "crit_dmg_pct" or "attack_speed_pct" => AffixFamily.Precision,
            "flat_hp" or "flat_armor" or "dodge_chance_pct" or "block_chance_pct" or "lifesteal_pct" or "thorns_pct" => AffixFamily.Guard,
            "loot_luck_pct" or "gold_find_pct" => AffixFamily.Fortune,
            "gather_speed_pct" or "gather_yield_pct" or "gather_rare_find_pct" => AffixFamily.Craft,
            _ => AffixFamily.None,
        };

        /// <summary>The boost a family gets from the tree, in tenths of a percent (100 = +10%).</summary>
        public static int BoostTenths(in TickStatePayload payload, AffixFamily family)
        {
            int id = family switch
            {
                AffixFamily.Power => WarTreeRegistry.PowerAffinity,
                AffixFamily.Precision => WarTreeRegistry.PrecisionAffinity,
                AffixFamily.Guard => WarTreeRegistry.GuardAffinity,
                AffixFamily.Fortune => WarTreeRegistry.FortuneAffinity,
                AffixFamily.Craft => HearthTreeRegistry.CraftAffinity,
                _ => -1,
            };
            if (id < 0) return 0;
            if (family != AffixFamily.Craft && payload.SkillTreeVersion != WarTreeRegistry.CurrentVersion) return 0;
            int rank = payload.SkillRanks[id];
            if (rank <= 0) return 0;
            if (family == AffixFamily.Craft)
            {
                var hearth = HearthTreeRegistry.Get(id);
                return hearth == null ? 0 : hearth.ValueAt(Math.Min(rank, hearth.MaxLevel));
            }
            var war = WarTreeRegistry.Get(id);
            return war == null ? 0 : war.ValueAt(Math.Min(rank, war.MaxLevel));
        }

        /// <summary>The character's cached totals with each family's affix part raised by its tree boost.</summary>
        public static EquippedAffixTotals Totals(in TickStatePayload payload)
        {
            EquippedAffixTotals t = payload.CachedAffixTotals;
            int power = BoostTenths(in payload, AffixFamily.Power);
            int precision = BoostTenths(in payload, AffixFamily.Precision);
            int guard = BoostTenths(in payload, AffixFamily.Guard);
            int fortune = BoostTenths(in payload, AffixFamily.Fortune);
            if ((power | precision | guard | fortune) == 0) return t;

            ref readonly var a = ref payload.CachedAffixTotals.AffixPart;
            static int Up(int part, int boostTenths) => (int)((long)part * boostTenths / 1000L);

            t.DamageTenthsPct += Up(a.DamageTenthsPct, power);
            t.FlatArmorPenetration += Up(a.FlatArmorPenetration, power);
            t.CrushingChanceTenthsPct += Up(a.CrushingChanceTenthsPct, power);
            t.WorldBossDamageTenthsPct += Up(a.WorldBossDamageTenthsPct, power);

            t.CritChanceTenthsPct += Up(a.CritChanceTenthsPct, precision);
            t.CritDamageTenthsPct += Up(a.CritDamageTenthsPct, precision);
            t.AttackSpeedTenthsPct += Up(a.AttackSpeedTenthsPct, precision);

            t.FlatHp += Up(a.FlatHp, guard);
            t.FlatDefense += Up(a.FlatDefense, guard);
            t.DodgeTenthsPct += Up(a.DodgeTenthsPct, guard);
            t.BlockTenthsPct += Up(a.BlockTenthsPct, guard);
            t.LifestealTenthsPct += Up(a.LifestealTenthsPct, guard);
            t.ThornsTenthsPct += Up(a.ThornsTenthsPct, guard);

            t.LootLuckTenthsPct += Up(a.LootLuckTenthsPct, fortune);
            t.GoldTenthsPct += Up(a.GoldTenthsPct, fortune);
            return t;
        }

        /// <summary>A tool affix sum (speed, yield or rare find, in whole percent) with the Craft boost.</summary>
        public static int CraftScaled(in TickStatePayload payload, int toolAffixPct)
        {
            int craft = BoostTenths(in payload, AffixFamily.Craft);
            return craft == 0 ? toolAffixPct : toolAffixPct + (int)((long)toolAffixPct * craft / 1000L);
        }
    }
}
