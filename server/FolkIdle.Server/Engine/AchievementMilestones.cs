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
        // Modul: Achievement claim button. The pre-existing, player-claimed
        // "kill 10000 monsters" achievement (id 1, see SimulationEngine's
        // CommandType.ClaimAchievementReward handler / AchievementClaimQueue)
        // predates this tiered I-IV family and isn't tiered itself - it's a
        // single threshold/reward pair. Hoisted here (out of AchievementEngine's
        // previous inline literals) so GetNextTierTarget/GetNextTierReward can
        // report a real number instead of the 0 fallback every other
        // achievementId hits, which the client was rendering as a nonsensical
        // "0 / MAX".
        public const int MonsterKillAchievementId = 1;
        public const long MonsterKillThreshold = 10_000L;
        public const int MonsterKillReward = 500;

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

        // Modul: Phase - Full-Stack Production Polish, Part 2.3. Tiers II-IV
        // were previously long.MaxValue placeholders - mathematically
        // unattainable, so no player could ever cross them regardless of
        // how many harvest loops they completed. Replaced with a realistic
        // linear-then-exponential goal curve (10x growth per tier past I,
        // matching the task's own "10k, 100k, 1M" example scale).
        private static readonly long[] LogisticsThresholds = { 10_000L, 100_000L, 1_000_000L, 10_000_000L };
        private static readonly int[] LogisticsRewards = { 10, 50, 200, 800 };

        // Modul: stackable, permanent stat-bonus reward layered alongside
        // the PremiumDiamonds reward above - flat percentage points of
        // gathering speed (see LogisticsGatheringSpeedBonusPct on
        // PlayerRecord/TickStatePayload), summed the same way diamonds are
        // via GetStatBonusForTiersCrossed below. Only Logistics has a stat-
        // bonus reward track; Treasury/Forging remain diamonds-only.
        private static readonly int[] LogisticsStatBonusPctRewards = { 1, 2, 4, 8 };

        // Modul: A NAME FOR EACH. The Progress screen listed these as
        // "Achievement #1" to "#4" because nothing on the server named them -
        // the ids were the only handle, and an id is not something a player
        // can want. Kept here beside the thresholds they describe.
        public static string TitleFor(int achievementId) => achievementId switch
        {
            MonsterKillAchievementId => "Monster Slayer",
            TreasuryAchievementId => "Treasury",
            ForgingAchievementId => "Master Smith",
            LogisticsAchievementId => "Logistics",
            _ => $"Achievement {achievementId}",
        };

        public static string DescriptionFor(int achievementId) => achievementId switch
        {
            MonsterKillAchievementId => "Kill ten thousand monsters.",
            TreasuryAchievementId => "Hold gold at one time: 100,000, 5 million, 100 million, then 2.5 billion.",
            ForgingAchievementId => "Fuse at the Forge 50, then 500 times; then fuse an item up to rarity 10, then 14.",
            LogisticsAchievementId => "Gather 10,000 times, then 100,000, a million and ten million. Every tier also adds permanent gathering speed.",
            _ => string.Empty,
        };

        // Modul: THE LIFETIME CHAPTER (task 57). The four achievements moved
        // into the Book of Deeds as named tiers, paid automatically - there is
        // no claim button any more, same as deeds. The names live here beside
        // the thresholds and rewards they describe, and the goals and diamonds
        // are READ from those tables, so the book cannot quote a number the
        // award does not pay.
        public sealed record LifetimeTier(string Name, string Goal, int Diamonds);

        public static readonly int[] LifetimeAchievementIds =
        {
            MonsterKillAchievementId, TreasuryAchievementId, ForgingAchievementId, LogisticsAchievementId,
        };

        private static readonly string[] TreasuryTierNames = { "Purse", "Coffer", "Vault", "Hoard" };
        private static readonly string[] ForgingTierNames = { "Apprentice", "Journeyman", "Master", "Grandmaster" };
        private static readonly string[] LogisticsTierNames = { "Porter", "Carter", "Quartermaster", "Logistician" };

        public static System.Collections.Generic.IReadOnlyList<LifetimeTier> TiersFor(int achievementId)
        {
            var tiers = new System.Collections.Generic.List<LifetimeTier>(4);
            switch (achievementId)
            {
                case MonsterKillAchievementId:
                    tiers.Add(new LifetimeTier("Monster Slayer", $"{MonsterKillThreshold:N0} kills", MonsterKillReward));
                    break;
                case TreasuryAchievementId:
                    for (int i = 0; i < TreasuryThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(TreasuryTierNames[i], $"Hold {TreasuryThresholds[i]:N0} gold at once", TreasuryRewards[i]));
                    break;
                case ForgingAchievementId:
                    for (int i = 0; i < ForgingUpgradeCountThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(ForgingTierNames[i], $"{ForgingUpgradeCountThresholds[i]:N0} fusions", ForgingRewards[i]));
                    for (int i = 0; i < ForgingSynthesisTierThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(ForgingTierNames[i + 2], $"Fuse a piece up to rarity {ForgingSynthesisTierThresholds[i]}", ForgingRewards[i + 2]));
                    break;
                case LogisticsAchievementId:
                    for (int i = 0; i < LogisticsThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(LogisticsTierNames[i], $"{LogisticsThresholds[i]:N0} harvests, +{LogisticsStatBonusPctRewards[i]}% gathering speed", LogisticsRewards[i]));
                    break;
            }
            return tiers;
        }

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

        // Modul: mirrors GetDiamondsForTiersCrossed's summation exactly, but
        // over the stat-bonus table - currently only Logistics has one, so
        // every other achievementId returns 0 (a genuine no-bonus result,
        // not a missing-data fallback).
        public static int GetStatBonusForTiersCrossed(int achievementId, int previousTier, int newTier)
        {
            if (achievementId != LogisticsAchievementId)
            {
                return 0;
            }

            int total = 0;
            int clampedNewTier = System.Math.Min(newTier, LogisticsStatBonusPctRewards.Length);
            for (int tier = previousTier + 1; tier <= clampedNewTier; tier++)
            {
                total += LogisticsStatBonusPctRewards[tier - 1];
            }
            return total;
        }

        public static long GetNextTierTarget(int achievementId, int completedTier)
        {
            if (achievementId == MonsterKillAchievementId)
            {
                return completedTier == 0 ? MonsterKillThreshold : 0L;
            }

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
            if (achievementId == MonsterKillAchievementId)
            {
                return completedTier == 0 ? MonsterKillReward : 0;
            }

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
