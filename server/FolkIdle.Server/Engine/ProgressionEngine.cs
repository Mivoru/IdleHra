using System;
using System.Runtime.InteropServices;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LineageDefinition
    {
        public int Id;
        public int DamageScalePerLevelPct;
        public int HpScalePerLevelPct;
    }

    public static class ProgressionEngine
    {
        // Modul: balance pass. THE single authority for the level-up curve.
        //
        // This formula used to be copy-pasted into three call sites
        // (ProgressionEngine below, SimulationEngine's Chrono XP warp and
        // OfflineSimulationEngine's catch-up projection), each carrying a
        // "must stay in sync" comment and no mechanism to enforce it. It is
        // now defined once and called from all three.
        //
        // The curve was 100 * 1.15^level - 16.4x per 20-level region against a
        // 3x-per-region gear curve, so the requirement outran player power by
        // ~5x every region and compounded. Level 100 needed 718 million XP,
        // roughly 59 days of uninterrupted combat. Regions 3-5 were not slow,
        // they were unreachable. That was corrected to 400 * 1.06^level, which
        // tracked the gear curve so exactly that time-per-region went FLAT:
        // about 72 / 123 / 163 / 190 / 209 minutes, or the whole game in
        // thirteen hours.
        //
        // Modul: SEASONS, 2026-08-05. Thirteen hours is the wrong length for a
        // game that wipes every three months.
        //
        // A season has to be worth entering, which means the top of the ladder
        // must NOT be reachable in the first one. The intent is that a normal
        // player clears four regions in season one and finishes the fifth in
        // season two or three - helped by whatever the reset carries forward
        // (the village, race bonuses, inheritance stats). Someone who rolls
        // exceptional gear should get there sooner; nobody should get there in
        // ninety days from a standing start.
        //
        // Flat pacing is exactly wrong for that. The curve has to be BACK-
        // LOADED: quick enough at the start that a first evening shows real
        // movement, steep enough at the top that the last region is a season's
        // work. 1.13 per level is 12.1x per region against the 3x gear curve,
        // so each region costs about four times the one before it.
        //
        // The base drops 400 -> 250 to keep the opening snappy while the
        // exponent carries the back half. Modelled on weapon base power alone,
        // ignoring affixes, sets, crit and attack speed - so a FLOOR, and a
        // geared player runs perhaps three times faster:
        //
        //     region   1      2      3       4        5
        //     floor    2.5h   10.9h  47.6h   197h     784h
        //     geared   0.8h   3.6h   15.9h   66h      261h
        //     cumulative geared: 0.8 / 4.5 / 20 / 86 / 347 hours
        //
        // At a dedicated 200 active hours a season that is region 4 cleared
        // inside season one and region 5 finished in season two; a casual pace
        // takes three or four. ProgressionRateTests prints these against the
        // real tick - change either constant and read the new numbers there
        // rather than re-deriving them here.
        //
        // Both levers deliberately live in this one pair. Monster HP and XP are
        // untouched, so the XP = MaxHp/5 identity that makes this analytically
        // solvable at all still holds.
        //
        public const double LevelCurveBase = 250.0;
        // Modul: 1.13 -> 1.16, and it is the offline cap that forced it.
        //
        // The season was sized against "200 active hours" - an assumption about
        // how long someone sits at the screen, which is the wrong quantity for
        // a game that runs while they do not. Offline catch-up banks up to
        // twelve hours per absence, so a player returning twice a day collects
        // twenty-four hours of progress every real day: 2,160 hours in a
        // ninety-day season. At 1.13 the whole game was 988 hours, finished in
        // about three weeks.
        //
        // At 1.16 each region costs about 6.5x the one before it rather than
        // 4x, so regions 1-4 still fall inside the first season and region 5
        // alone outlasts one. Finishing lands in the second or third season,
        // which is where it was always meant to.
        //
        // It also puts the weight where the content is: region 5 is 86% of the
        // game now rather than 76%, and that is the stretch where races,
        // character slots and the deeper tools unlock.
        public const double LevelCurveGrowth = 1.16;

        /// <summary>
        /// The health pool a character has before lineage, CON and affixes -
        /// the floor under every health bar in the game.
        ///
        /// Modul: IT WAS A FLAT 100, AND MONSTERS WERE GEOMETRIC, 2026-09-06.
        ///
        /// `baseMilliHp = 100000L` was a CONSTANT at both sites that compute a
        /// health pool, so the only level term in a player's bar was the
        /// lineage percentage - which is LINEAR (`base * (1 + pct * level/100)`)
        /// and, for a Warrior, is zero. Monster attack power over the same five
        /// regions runs 40 -> 500 -> 2,100 -> 8,725 -> 36,400 for the strongest
        /// regular: geometric, about 4.2x a region.
        ///
        /// Linear against geometric has exactly one outcome, and
        /// ProgressionRateTests had been PRINTING it for months without
        /// asserting on it - the strongest regular of a region, as a share of
        /// the geared health bar per second:
        ///
        ///   region 1   9.5%     region 4   64.4%
        ///   region 2  17.0%     region 5  104.0%
        ///   region 3  33.2%
        ///
        /// Over 100% means an ordinary region-5 regular empties a fully geared
        /// bar in under a second, and Malakor's 118,400 is an instant death that
        /// no gear, no food and no amount of healing can survive - a single blow
        /// larger than the whole bar is not a fight, it is a wall. That is the
        /// "boss instakills me" report, and it was structural rather than tuning.
        ///
        /// 9.2% a level compounding is about 5.8x every twenty levels, which is
        /// a region: the bar now climbs on the same shape as the thing hitting
        /// it. Level 1 is still exactly 100, so nothing about the opening hour
        /// moves.
        ///
        ///   level      1     21      41      61       81      101
        ///   old      100    100     100     100      100      100
        ///   new      100    581   3,379  19,649  114,238  664,146
        ///
        /// TUNED AGAINST THE TABLE, NOT PICKED. 6.5% and 8% were both measured
        /// first and both left region 5 above a third of the geared bar per
        /// second; ProgressionRateTests prints the share for every region and
        /// now ASSERTS on it, which is the thing that was missing when this
        /// reached 104%.
        ///
        /// The lineage percentage still layers on top and still differentiates
        /// Tank from Warrior - but a Warrior, whose HpScalePerLevelPct is 0, now
        /// has a health curve at all for the first time.
        /// </summary>
        public const double HpCurveGrowthPerLevel = 1.092;

        public const long BaseMilliHpAtLevelOne = 100_000L;

        public static long BaseMilliHpForLevel(int level)
        {
            if (level <= 1) return BaseMilliHpAtLevelOne;
            double scaled = BaseMilliHpAtLevelOne * Math.Pow(HpCurveGrowthPerLevel, level - 1);
            return scaled >= long.MaxValue ? long.MaxValue : (long)scaled;
        }

        public static long GetRequiredXpForLevel(int currentLevel)
        {
            return (long)Math.Ceiling(LevelCurveBase * Math.Pow(LevelCurveGrowth, currentLevel));
        }

        // Static read-only span of available lineages
        public static readonly LineageDefinition[] Lineages = new LineageDefinition[]
        {
            new LineageDefinition { Id = 0, DamageScalePerLevelPct = 0, HpScalePerLevelPct = 0 }, // Fallback / No Lineage
            new LineageDefinition { Id = 1, DamageScalePerLevelPct = 5, HpScalePerLevelPct = 0 }, // Warrior
            new LineageDefinition { Id = 2, DamageScalePerLevelPct = 0, HpScalePerLevelPct = 8 }, // Tank
        };

        public static void ProcessMonsterDeath(ref TickStatePayload payload, int baseExpReward, int xpMultiplier, int activeGlobalEventId, int activeRaceId = 0)
        {
            if (activeGlobalEventId == 2) // BloodMoonVanguard
            {
                xpMultiplier += 15;
            }
            int effectiveXp = (baseExpReward * xpMultiplier) / 100;

            // Modul 13.4.3: -20% character XP generation while an early
            // mentorship termination penalty is active (see MentorshipEngine).
            if (payload.XpPenaltyExpiresEpoch > DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            {
                effectiveXp = (int)(effectiveXp * 0.8f);
            }

            payload.CurrentXp += effectiveXp;
            
            // Validate lineage bounds
            if (payload.SelectedLineageId < 0 || payload.SelectedLineageId >= Lineages.Length)
            {
                payload.SelectedLineageId = 0;
            }

            bool leveledUp = false;
            int levelsGained = 0;

            while (true)
            {
                long requiredXp = GetRequiredXpForLevel(payload.CurrentLevel);

                if (payload.CurrentXp >= requiredXp)
                {
                    payload.CurrentXp -= requiredXp;
                    payload.CurrentLevel++;
                    leveledUp = true;
                    levelsGained++;
                }
                else
                {
                    break;
                }
            }

            if (leveledUp)
            {
                RaceAttributeGrowth.ApplyLevelUpGrowth(ref payload, activeRaceId, levelsGained);

                // Modul: ONE SKILL POINT PER LEVEL, granted HERE.
                //
                // Reported as "I am level 20 and have no points to spend", and
                // that was exact. The game grows levels in three places - this
                // one, the warp/bulk catch-up in SimulationEngine, and the
                // offline projection - and only the warp path paid the point.
                // This is the path an ordinary kill takes, so a player who
                // simply played the game earned nothing, forever.
                //
                // The other two grant it as well now. Putting it in the
                // authority that owns the level-up loop is what stops the next
                // copy of that loop from forgetting again.
                payload.AvailableSkillPoints += levelsGained;
                payload.IsDirty = true;
            }
        }
    }
}
