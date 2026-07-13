using System;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public static class TimeWarpProcessingEngine
    {
        public static void ResolveInstantWarp(ref TickStatePayload payload, int warpDurationSeconds)
        {
            if (warpDurationSeconds <= 0) return;
            
            // If no active action, warp acts as chrono-bank injection
            if (payload.ActiveActivityId <= 0)
            {
                double refund = Math.Min(warpDurationSeconds, 604800.0 - payload.BankedChronoSeconds);
                if (refund > 0)
                {
                    payload.BankedChronoSeconds += refund;
                    payload.IsDirty = true;
                }
                return;
            }

            double baseActionTime = payload.RequiredProgressTicks / 10.0;
            double speedModifier = payload.SpeedMultiplier / 100.0;
            
            if (baseActionTime <= 0) return;

            double actionTime = baseActionTime * (1.0 - speedModifier);
            if (actionTime < 0.1) actionTime = 0.1; // clamp

            long totalActions = (long)Math.Floor(warpDurationSeconds / actionTime);

            if (totalActions > payload.InventorySpaceRemaining)
            {
                totalActions = payload.InventorySpaceRemaining;
            }

            if (totalActions > 0)
            {
                payload.InventorySpaceRemaining -= (int)totalActions;
                
                // Aggregate yields injection (Ores, XP, Mastery deferred safely to write-behind)
                payload.CurrentXp += totalActions * 15; // Base XP heuristic 
                
                if (payload.ActiveActivityId >= 1000 && payload.ActiveActivityId < 2000)
                    payload.WoodcuttingMasteryXp += (int)(totalActions * 5);
                else if (payload.ActiveActivityId >= 2000 && payload.ActiveActivityId < 3000)
                    payload.MiningMasteryXp += (int)(totalActions * 5);
                    
                payload.CurrentProgressTicks = 0;
            }
            
            payload.IsDirty = true;
        }
    }
}
