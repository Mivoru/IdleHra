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
            // Modul 13.4.3: Human's innate +5% Gold acquisition passive (and
            // every other StatsCalculator gold source).
            gold = (long)(gold * (1.0f + goldAcquisitionMultiplierPct / 100f));
            gold = (long)(gold * (1.0f + LegacyPerkResolver.GetGoldBonusPct(payload.CachedLegacyPerks) / 100f));
            gold = (long)(gold * (1.0f + GuildBonusesCache.GetBuffTier(payload.GuildId, "Gold") * 0.02f));
            // Modul: inheritance. A permanent, season-crossing multiplier.
            gold = (long)(gold * (1.0f + InheritanceRegistry.GetBonusPct(payload.Inherit_GoldGain) / 100f));

            if (payload.Skill_TrophyHunter > 0
                && RaceUnlockRegistry.GetRegionForBossMonsterId(monster.Id) > 0)
            {
                gold = (long)(gold * (1.0f + SkillTreeRegistry.GetBonusPercent(
                    SkillTreeRegistry.BoughTrophyHunter, payload.Skill_TrophyHunter) / 100f));
            }

            return gold;
        }
    }
}
