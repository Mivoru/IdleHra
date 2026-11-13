using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>One aptitude milestone: the aptitude value that unlocks it and what it adds.</summary>
    public readonly record struct AptitudeMilestone(int Aptitude, int At, string Name, string Effect);

    /// <summary>
    /// Aptitude milestones (owner decision 2026-11-13: "four aptitudes plus
    /// milestones"). At 30, 40 and 50 points each aptitude unlocks a second
    /// bonus in its own field - a sub-stat that comes from the bloodline,
    /// without a fifth breeding axis.
    /// </summary>
    /// <remarks>
    /// Modul: WHY. The main bonus (BreedingAptitudes.BonusPercentFor) falls to
    /// 0.3% a point past 35, so the last fifteen points of an aptitude were
    /// worth +4.5% together - while the lineage overhaul made 50/50/50/50 a
    /// month-long goal. The diminishing curve stays (it keeps account age from
    /// deciding the board); the milestones make the tail worth climbing.
    ///
    /// Modul: SIZES, against what already pays the same thing: gold +15% at
    /// 50 Fortune is under the Ancestors' Fortune boon (+25%); block +10% at 50
    /// Endurance is under one War-tree block node line and still meets the 75%
    /// cap; crit damage +0.30x at 50 Strength is a third of a crit's base
    /// bonus; yield +15 points at 50 Skill matches one Great Works stage set.
    ///
    /// Modul: ONE CODE PATH. Each is read by the helper the live tick, the
    /// offline window, the dungeon and the stat sheet already share -
    /// LiveCritMultiplier, LiveCombatStats (block), GatheringYieldFor and
    /// CombatGoldReward.FactorsFor - so a milestone cannot be dodged offline.
    /// The values are on the active register's aptitudes, like the main bonus.
    /// </remarks>
    public static class AptitudeMilestones
    {
        public static readonly int[] Thresholds = { 30, 40, 50 };

        private static int Reached(int aptitude)
        {
            int n = 0;
            foreach (int t in Thresholds) if (aptitude >= t) n++;
            return n;
        }

        /// <summary>Strength: extra crit damage, as a multiplier increment (0.10 / 0.20 / 0.30).</summary>
        public static float CritDamageBonus(int strength) => Reached(strength) switch
        {
            0 => 0f,
            1 => 0.10f,
            2 => 0.20f,
            _ => 0.30f,
        };

        /// <summary>Skill: extra gathering yield, in percentage points (5 / 10 / 15).</summary>
        public static int GatherYieldPct(int skill) => Reached(skill) * 5;

        /// <summary>Endurance: extra block, in percent (3 / 6 / 10).</summary>
        public static float BlockPct(int endurance) => Reached(endurance) switch
        {
            0 => 0f,
            1 => 3f,
            2 => 6f,
            _ => 10f,
        };

        /// <summary>Fortune: extra gold from kills, in percent (5 / 10 / 15).</summary>
        public static int GoldPct(int fortune) => Reached(fortune) * 5;

        /// <summary>Every milestone, for the Bloodline screen and the Wiki.</summary>
        public static IReadOnlyList<AptitudeMilestone> All { get; } = Build();

        private static List<AptitudeMilestone> Build()
        {
            var list = new List<AptitudeMilestone>(16);
            string[] crit = { "+0.10x crit damage", "+0.20x crit damage", "+0.30x crit damage" };
            string[] yield = { "+5% gathering yield", "+10% gathering yield", "+15% gathering yield" };
            string[] block = { "+3% block", "+6% block", "+10% block" };
            string[] gold = { "+5% gold from kills", "+10% gold from kills", "+15% gold from kills" };
            for (int i = 0; i < Thresholds.Length; i++)
            {
                list.Add(new AptitudeMilestone(BreedingAptitudes.Strength, Thresholds[i], "Keen Edge", crit[i]));
                list.Add(new AptitudeMilestone(BreedingAptitudes.Skill, Thresholds[i], "Deft Hands", yield[i]));
                list.Add(new AptitudeMilestone(BreedingAptitudes.Endurance, Thresholds[i], "Stout Frame", block[i]));
                list.Add(new AptitudeMilestone(BreedingAptitudes.Fortune, Thresholds[i], "Lucky Purse", gold[i]));
            }
            return list;
        }
    }
}
