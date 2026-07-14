using System;
using System.Runtime.InteropServices;

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
                // Must execute using 64-bit integer math to prevent overflow
                long requiredXp = 100L * payload.CurrentLevel * payload.CurrentLevel;

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
