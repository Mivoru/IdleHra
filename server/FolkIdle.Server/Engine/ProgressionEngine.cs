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
        public const double LevelCurveGrowth = 1.13;

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
                payload.IsDirty = true;
            }
        }
    }
}
