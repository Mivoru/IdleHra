using System;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    public static class ChronoBufferEngine
    {
        public const int OfflineThresholdSeconds = 86400;
        public const int MaxBankedChronoSeconds = 604800;
        private const double ExcessReservoirScale = 1200.0;

        public static int CalculateOfflineBankedSeconds(long rawElapsedOfflineSeconds)
        {
            if (rawElapsedOfflineSeconds <= 0)
            {
                return 0;
            }

            if (rawElapsedOfflineSeconds <= OfflineThresholdSeconds)
            {
                return ClampBankedSeconds(rawElapsedOfflineSeconds);
            }

            long excess = rawElapsedOfflineSeconds - OfflineThresholdSeconds;
            double decayedExcess = Math.Log(excess + 1.0) * ExcessReservoirScale;
            long earned = OfflineThresholdSeconds + Math.Max(0L, (long)Math.Floor(decayedExcess));
            return ClampBankedSeconds(earned);
        }

        public static int AddBankedSeconds(int currentBankedSeconds, long earnedSeconds)
        {
            long value = (long)Math.Max(0, currentBankedSeconds) + Math.Max(0L, earnedSeconds);
            return ClampBankedSeconds(value);
        }

        public static int ClampBankedSeconds(double seconds)
        {
            if (seconds <= 0.0)
            {
                return 0;
            }

            if (seconds >= MaxBankedChronoSeconds)
            {
                return MaxBankedChronoSeconds;
            }

            return (int)Math.Floor(seconds);
        }

        public static int ClampBankedSeconds(long seconds)
        {
            if (seconds <= 0L)
            {
                return 0;
            }

            if (seconds >= MaxBankedChronoSeconds)
            {
                return MaxBankedChronoSeconds;
            }

            return (int)seconds;
        }

        public static bool IsValidSpeedMultiplier(double multiplier)
        {
            return multiplier == 1.0 || multiplier == 2.0 || multiplier == 4.0;
        }

        public static void ProcessLoginHandshake(AccountChronoRegistry registry, long currentTimestamp)
        {
            if (registry.LastClockSyncEpoch == 0)
            {
                registry.LastClockSyncEpoch = currentTimestamp;
                registry.BankedChronoSeconds = ClampBankedSeconds(registry.BankedChronoSeconds);
                if (!IsValidSpeedMultiplier(registry.ActiveSpeedMultiplier))
                {
                    registry.ActiveSpeedMultiplier = 1.0;
                }
                return;
            }

            long deltaSeconds = currentTimestamp - registry.LastClockSyncEpoch;
            int earnedSeconds = CalculateOfflineBankedSeconds(deltaSeconds);
            registry.BankedChronoSeconds = AddBankedSeconds(registry.BankedChronoSeconds, earnedSeconds);
            registry.LastClockSyncEpoch = currentTimestamp;

            if (!IsValidSpeedMultiplier(registry.ActiveSpeedMultiplier))
            {
                registry.ActiveSpeedMultiplier = 1.0;
            }
        }

        public static void ProcessLoginHandshake(PlayerChronoRegistry registry, long currentTimestamp)
        {
            if (registry.LastDisconnectTimestamp == 0)
            {
                registry.LastDisconnectTimestamp = currentTimestamp;
                return;
            }

            long deltaSeconds = currentTimestamp - registry.LastDisconnectTimestamp;
            if (deltaSeconds <= 0)
            {
                return;
            }

            int earnedSeconds = CalculateOfflineBankedSeconds(deltaSeconds);
            registry.BankedChronoSeconds = (uint)AddBankedSeconds((int)Math.Min(int.MaxValue, registry.BankedChronoSeconds), earnedSeconds);
            registry.LastDisconnectTimestamp = currentTimestamp;
        }
    }
}
