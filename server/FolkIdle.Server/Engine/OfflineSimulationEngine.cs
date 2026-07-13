using System;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public static class OfflineSimulationEngine
    {
        private const long OneDaySeconds = 86400;
        private const long ThreeDaysSeconds = 259200;
        private const long SevenDaysSeconds = 604800;

        public static void ExtrapolateOfflineProgress(ref TickStatePayload payload, long currentUnixTimestamp)
        {
            if (payload.LastLogoutTimestamp == 0)
            {
                payload.LastLogoutTimestamp = currentUnixTimestamp;
                return;
            }

            long deltaSeconds = currentUnixTimestamp - payload.LastLogoutTimestamp;
            if (deltaSeconds <= 0) return;

            // 4-tier exponential efficiency decay matrix
            // Tier 1 (0-8h) = 1.0, Tier 2 (8-16h) = 0.5, Tier 3 (16-24h) = 0.25, Tier 4 (24h+) = 0.0
            double tEff = 0;
            long t1 = Math.Min(deltaSeconds, 28800); // 8 hours
            tEff += t1 * 1.0;
            long remaining = deltaSeconds - t1;
            
            if (remaining > 0)
            {
                long t2 = Math.Min(remaining, 28800);
                tEff += t2 * 0.5;
                remaining -= t2;
                
                if (remaining > 0)
                {
                    long t3 = Math.Min(remaining, 28800);
                    tEff += t3 * 0.25;
                }
            }

            double baseActionTime = payload.RequiredProgressTicks / 10.0;
            double speedModifier = payload.SpeedMultiplier / 100.0;
            
            if (baseActionTime > 0 && payload.ActiveActivityId > 0)
            {
                double actionTime = baseActionTime * (1.0 - speedModifier);
                if (actionTime < 0.1) actionTime = 0.1; // clamp to prevent div by zero
                
                long totalActions = (long)Math.Floor(tEff / actionTime);
                long overflowTime = 0;

                if (totalActions > payload.InventorySpaceRemaining)
                {
                    long allowedActions = payload.InventorySpaceRemaining;
                    double usedTeff = allowedActions * actionTime;
                    double unusedTeff = tEff - usedTeff;
                    overflowTime = (long)unusedTeff;
                    totalActions = allowedActions;
                }

                if (totalActions > 0)
                {
                    payload.InventorySpaceRemaining -= (int)totalActions;
                    payload.CurrentProgressTicks = 0;
                    // Note: Actual item inserts are deferred to Redis write-behind utilizing the modified payload limits
                }

                if (overflowTime > 0)
                {
                    double refund = Math.Min(overflowTime, 604800.0 - payload.BankedChronoSeconds);
                    if (refund > 0)
                    {
                        payload.BankedChronoSeconds += refund;
                    }
                }
            }
            else
            {
                // If no active action, all time is overflow
                double refund = Math.Min(deltaSeconds, 604800.0 - payload.BankedChronoSeconds);
                if (refund > 0)
                {
                    payload.BankedChronoSeconds += refund;
                }
            }

            payload.LastLogoutTimestamp = currentUnixTimestamp;
            payload.IsDirty = true;
        }
    }
}
