using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The gold one kill pays, with every per-player multiplier applied.
    /// The live tick and the offline projection both call this.
    /// </summary>
    /// <remarks>
    /// Modul: ONE FORMULA FOR BOTH KILL PATHS, BY THE OWNER'S DECISION (2026-09-25).
    ///
    /// The offline copy had its own formula. It
    /// applied race and inheritance and left out the legacy gold perk, the
    /// guild Gold buff and Trophy Hunter, and it applied inheritance as an
    /// additive integer BEFORE race rather than as a multiplier after it. So
    /// a character earned less asleep than awake. That is the same kind of
    /// drift this codebase already shipped once, in gathering. The owner
    /// ruled that offline pays exactly what online pays, so both paths call
    /// this function now. CombatGoldParityTests pins it.
    ///
    /// The ORDER matters: each step truncates to a long, so reordering the
    /// steps changes the result by a coin or two on small rewards.
    /// </remarks>
    public static class CombatGoldReward
    {
        public static long PerKill(in TickStatePayload payload, in MonsterDefinition monster, float goldAcquisitionMultiplierPct)
        {
            long gold = EconomyDecisions.BaseCombatGold(monster.BaseGoldReward);
            var f = FactorsFor(in payload, goldAcquisitionMultiplierPct);
            gold = (long)(gold * f.Acquisition);
            gold = (long)(gold * f.Legacy);
            gold = (long)(gold * f.Guild);
            gold = (long)(gold * f.Inheritance);
            gold = (long)(gold * f.Pet);

            if (payload.Skill_TrophyHunter > 0
                && RaceUnlockRegistry.GetRegionForBossMonsterId(monster.Id) > 0)
            {
                gold = (long)(gold * (1.0f + SkillTreeRegistry.GetBonusPercent(
                    SkillTreeRegistry.BoughTrophyHunter, payload.Skill_TrophyHunter) / 100f));
            }

            return gold;
        }

        /// <summary>The per-player multipliers a kill's gold passes through, in PerKill's order.</summary>
        public readonly record struct GoldFactors(float Acquisition, float Legacy, float Guild, float Inheritance, float Pet)
        {
            /// <summary>All of them as one percentage bonus, untruncated - what the stat sheet prints.</summary>
            public double TotalBonusPct => ((double)Acquisition * Legacy * Guild * Inheritance * Pet - 1.0) * 100.0;
        }

        /// <summary>
        /// Every gold multiplier except Trophy Hunter, which applies to bosses
        /// only. PerKill and CharacterStatSheet both read this, so the sheet's
        /// gold bonus is the one a kill is paid at.
        /// </summary>
        public static GoldFactors FactorsFor(in TickStatePayload payload, float goldAcquisitionMultiplierPct)
            => new(
                // Modul 13.4.3: Human's innate +5% Gold acquisition passive (and
                // every other StatsCalculator gold source).
                1.0f + goldAcquisitionMultiplierPct / 100f,
                1.0f + LegacyPerkResolver.GetGoldBonusPct(payload.CachedLegacyPerks) / 100f,
                1.0f + GuildBonusesCache.GetBuffTier(payload.GuildId, "Gold") * 0.02f,
                // Modul: inheritance. A permanent, season-crossing multiplier.
                1.0f + InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100f,
                // The character's pet (PetRegistry), on the active slot's totals.
                1.0f + payload.CachedAffixTotals.GoldTenthsPct / 1000f);
    }
}
