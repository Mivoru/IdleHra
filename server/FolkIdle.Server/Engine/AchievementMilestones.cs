namespace FolkIdle.Server.Engine
{
    // Modul 13: static threshold/reward tables for the auto-awarded tiered
    // (I-IV) achievements, evaluated during StateCheckpointManager.FlushState.
    //
    // AchievementId note: 1 is already taken by the pre-existing, player-claimed
    // "kill 10000 monsters" achievement (see SimulationEngine's
    // CommandType.ClaimAchievementReward handler / AchievementClaimQueue). These
    // three new achievements use 2-4 to avoid colliding with it.
    public static class AchievementMilestones
    {
        public const int TreasuryAchievementId = 2;
        public const int ForgingAchievementId = 3;
        public const int LogisticsAchievementId = 4;

        private static readonly long[] TreasuryThresholds = { 100_000L, 5_000_000L, 100_000_000L, 2_500_000_000L };
        private static readonly int[] TreasuryRewards = { 10, 50, 250, 1000 };

        // Tier I/II are upgrade-count thresholds; Tier III/IV are a distinct
        // metric (highest QualityTier ever produced by a successful Forge
        // fusion), not a continuation of the same count.
        private static readonly int[] ForgingUpgradeCountThresholds = { 50, 500 };
        private static readonly int[] ForgingSynthesisTierThresholds = { 10, 14 };
        private static readonly int[] ForgingRewards = { 15, 75, 200, 1500 };

        // Only Tier I is defined for Logistics per the design; entries 1-3
        // (index 1-3) are unreachable placeholders so the reward-lookup helper
        // below can stay uniform across all three achievement types.
        private static readonly long[] LogisticsThresholds = { 5_000L, long.MaxValue, long.MaxValue, long.MaxValue };
        private static readonly int[] LogisticsRewards = { 10, 0, 0, 0 };

        public static int EvaluateTreasuryTier(long currentGold)
        {
            int tier = 0;
            for (int i = 0; i < TreasuryThresholds.Length; i++)
            {
                if (currentGold >= TreasuryThresholds[i]) tier = i + 1;
            }
            return tier;
        }

        public static int EvaluateForgingTier(int upgradeCount, int highestSynthesisTier)
        {
            int tier = 0;
            for (int i = 0; i < ForgingUpgradeCountThresholds.Length; i++)
            {
                if (upgradeCount >= ForgingUpgradeCountThresholds[i]) tier = i + 1;
            }
            for (int i = 0; i < ForgingSynthesisTierThresholds.Length; i++)
            {
                if (highestSynthesisTier >= ForgingSynthesisTierThresholds[i]) tier = i + 3;
            }
            return tier;
        }

        public static int EvaluateLogisticsTier(long harvestLoops)
        {
            int tier = 0;
            for (int i = 0; i < LogisticsThresholds.Length; i++)
            {
                if (harvestLoops >= LogisticsThresholds[i]) tier = i + 1;
            }
            return tier;
        }

        // Sums the reward for every tier strictly greater than previousTier up
        // to and including newTier (1-based tiers), so a flush that crosses more
        // than one tier at once still pays out correctly.
        public static int GetDiamondsForTiersCrossed(int achievementId, int previousTier, int newTier)
        {
            int[] rewardTable = achievementId switch
            {
                TreasuryAchievementId => TreasuryRewards,
                ForgingAchievementId => ForgingRewards,
                LogisticsAchievementId => LogisticsRewards,
                _ => System.Array.Empty<int>()
            };

            int total = 0;
            int clampedNewTier = System.Math.Min(newTier, rewardTable.Length);
            for (int tier = previousTier + 1; tier <= clampedNewTier; tier++)
            {
                total += rewardTable[tier - 1];
            }
            return total;
        }

        public static long GetNextTierTarget(int achievementId, int completedTier)
        {
            if (achievementId == ForgingAchievementId)
            {
                // Forging's two metrics don't share a unit, so the "next target"
                // for a client progress bar is expressed as whichever threshold
                // the next tier actually gates on.
                if (completedTier < ForgingUpgradeCountThresholds.Length) return ForgingUpgradeCountThresholds[completedTier];
                int synthesisIndex = completedTier - 2;
                if (synthesisIndex >= 0 && synthesisIndex < ForgingSynthesisTierThresholds.Length) return ForgingSynthesisTierThresholds[synthesisIndex];
                return 0L;
            }

            if (achievementId == TreasuryAchievementId)
            {
                return completedTier < TreasuryThresholds.Length ? TreasuryThresholds[completedTier] : 0L;
            }

            if (achievementId == LogisticsAchievementId)
            {
                return completedTier < LogisticsThresholds.Length ? LogisticsThresholds[completedTier] : 0L;
            }

            return 0L;
        }

        public static int GetNextTierReward(int achievementId, int completedTier)
        {
            int[] rewardTable = achievementId switch
            {
                TreasuryAchievementId => TreasuryRewards,
                ForgingAchievementId => ForgingRewards,
                LogisticsAchievementId => LogisticsRewards,
                _ => System.Array.Empty<int>()
            };

            if (completedTier >= rewardTable.Length) return 0;
            return rewardTable[completedTier];
        }
    }
}
