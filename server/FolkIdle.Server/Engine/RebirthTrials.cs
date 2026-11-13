using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>One Rebirth Trial: a rule for one life, and what finishing it pays for good.</summary>
    public sealed record RebirthTrial(
        int Id,
        string Name,
        string Rule,
        string Reward,
        string TitleSlug,
        string TitleName,
        // The rule, while the trial is active.
        int GoldPctDuring = 0,
        int XpPctDuring = 0,
        int MaxHpPctDuring = 0,
        bool ForbidsVault = false,
        // The reward, once completed.
        int GoldPctReward = 0,
        int XpPctReward = 0,
        int MaxHpPctReward = 0,
        int DamagePctReward = 0);

    /// <summary>
    /// Rebirth Trials (2026-11-13): an optional rule for the next life, chosen
    /// when the player is reborn. The trial is finished by the rebirth that
    /// ends that life - which needs RebirthRules.MinimumLevel anyway, so
    /// "finish" means "reach the rebirth level under the rule".
    /// </summary>
    /// <remarks>
    /// Modul: WHY (owner brief 2026-11-13, "endgame players need motivation").
    /// Both 20-rebirth accounts had nothing left that a rebirth could pay
    /// except shards. Challenges are how comparable prestige games (Antimatter
    /// Dimensions, NGU Idle, Realm Grinder) turn a repeated reset into new
    /// play: the same climb, under a rule that changes how it is played, for a
    /// permanent, bounded reward and a title.
    ///
    /// Modul: ONE CODE PATH. Each rule and each reward is applied in a helper
    /// that the live kill, the offline window and the projections already
    /// share - LiveKillXpMultiplierPct, CombatGoldReward.FactorsFor,
    /// EffectiveMaxMilliHpFor, EffectiveMilliAttackFor - so a trial cannot be
    /// dodged by playing offline. Completion is decided in exactly one place,
    /// RebirthEngine, at the rebirth that ends the life.
    ///
    /// Modul: BOUNDED. All four rewards together are +5% gold, +5% XP, +5%
    /// health and +2% damage - less than one inheritance stat - and each pays
    /// once. A trial already finished cannot be taken again.
    ///
    /// Modul: NO ABANDON in this first version. A life under a trial lasts
    /// until it reaches the rebirth level, like any other; the rules are mild
    /// enough (the harshest costs 40% of combat XP) that this is a slower
    /// climb, never a wall. The rebirth screen says so before the choice.
    /// </remarks>
    public static class RebirthTrials
    {
        public const int None = 0;
        public const int Want = 1;
        public const int SlowHand = 2;
        public const int Frailty = 3;
        public const int Iron = 4;

        public static IReadOnlyList<RebirthTrial> All { get; } = new List<RebirthTrial>
        {
            new(Want, "Trial of Want", "Kills pay half the gold.", "+5% gold from kills, for good",
                "trial_want", "The Frugal", GoldPctDuring: -50, GoldPctReward: 5),
            new(SlowHand, "Trial of the Slow Hand", "Combat XP is cut by 40%.", "+5% combat XP, for good",
                "trial_slow_hand", "The Patient", XpPctDuring: -40, XpPctReward: 5),
            new(Frailty, "Trial of Frailty", "Your health is cut by 25%.", "+5% health, for good",
                "trial_frailty", "The Unbroken", MaxHpPctDuring: -25, MaxHpPctReward: 5),
            new(Iron, "Trial of Iron", "Nothing goes into the Vault: you start this life with no kept gear.", "+2% damage, for good",
                "trial_iron", "Ironborn", ForbidsVault: true, DamagePctReward: 2),
        };

        public static RebirthTrial? Find(int id)
        {
            foreach (var trial in All) if (trial.Id == id) return trial;
            return null;
        }

        public static int BitOf(int id) => id is >= 1 and <= 30 ? 1 << (id - 1) : 0;

        public static bool IsCompleted(int completedMask, int id) => (completedMask & BitOf(id)) != 0;

        /// <summary>Whether a trial may be chosen for the next life.</summary>
        public static bool IsChoosable(int id, int completedMask) => Find(id) != null && !IsCompleted(completedMask, id);

        // --- the rule, while active -------------------------------------------

        public static int GoldPctDuring(int activeTrial) => Find(activeTrial)?.GoldPctDuring ?? 0;
        public static int XpPctDuring(int activeTrial) => Find(activeTrial)?.XpPctDuring ?? 0;
        public static int MaxHpPctDuring(int activeTrial) => Find(activeTrial)?.MaxHpPctDuring ?? 0;

        // --- the reward, once completed ---------------------------------------

        private static int Sum(int completedMask, Func<RebirthTrial, int> pick)
        {
            int total = 0;
            foreach (var trial in All) if (IsCompleted(completedMask, trial.Id)) total += pick(trial);
            return total;
        }

        public static int GoldPctReward(int completedMask) => Sum(completedMask, t => t.GoldPctReward);
        public static int XpPctReward(int completedMask) => Sum(completedMask, t => t.XpPctReward);
        public static int MaxHpPctReward(int completedMask) => Sum(completedMask, t => t.MaxHpPctReward);
        public static int DamagePctReward(int completedMask) => Sum(completedMask, t => t.DamagePctReward);

        /// <summary>The whole-life gold percent: the rule while active plus every reward earned.</summary>
        public static int GoldPct(int activeTrial, int completedMask) => GoldPctDuring(activeTrial) + GoldPctReward(completedMask);
        public static int XpPct(int activeTrial, int completedMask) => XpPctDuring(activeTrial) + XpPctReward(completedMask);
        public static int MaxHpPct(int activeTrial, int completedMask) => MaxHpPctDuring(activeTrial) + MaxHpPctReward(completedMask);
    }
}
