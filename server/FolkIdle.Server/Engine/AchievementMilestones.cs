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
        // Modul: 100, not the 500 first written here (task 57, 2026-09-29).
        // The 500 was never paid - the claim path's reward table was empty -
        // so moving it cost nobody anything. Against the diamond economy it was
        // far too much for a mid-game milestone: the Delve's calibrated tap is
        // 60 a week then (80 since 2026-10-31), ordinary kills pay about
        // the same, and the day-7 login bonus is 100. 500 would have been two
        // months of Delve for ten thousand kills. 100 sits beside the tier-II
        // rewards of the other three (Treasury 50, Master Smith 75, Logistics 50).
        public const int MonsterKillReward = 100;

        public const int TreasuryAchievementId = 2;
        public const int ForgingAchievementId = 3;
        public const int LogisticsAchievementId = 4;
        // Herbs and potions (2026-10-31): potions drunk, from the shelf or by
        // auto-drink, online or offline.
        public const int AlchemistAchievementId = 5;

        // Modul: ONE REWARD TABLE (owner A2, 2026-10-31). Tiers pay 5 / 15 / 40 /
        // 100 diamonds by how long they take (about an hour, a day, a week, a
        // month), and no tier pays more than 100. The old 1,000 and 1,500
        // tiers were one milestone worth 25 weeks of the Delve; their ladders
        // are split into more goals instead. Every OLD threshold is still a
        // threshold here, so the DiamondRework migration maps a stored tier to
        // its new index and nothing already paid is paid again or taken back.
        public const int BronzeDiamonds = 5;
        public const int SilverDiamonds = 15;
        public const int GoldDiamonds = 40;
        public const int LegendaryDiamonds = 100;

        private static readonly long[] TreasuryThresholds = { 100_000L, 1_000_000L, 5_000_000L, 25_000_000L, 100_000_000L, 500_000_000L, 2_500_000_000L };
        private static readonly int[] TreasuryRewards = { BronzeDiamonds, SilverDiamonds, SilverDiamonds, GoldDiamonds, GoldDiamonds, LegendaryDiamonds, LegendaryDiamonds };

        // The first ForgingCountTiers tiers are fusion counts; the rest are a
        // distinct metric (highest QualityTier ever produced by a successful
        // Forge fusion), not a continuation of the same count.
        public const int ForgingCountTiers = 3;
        private static readonly int[] ForgingUpgradeCountThresholds = { 50, 200, 500 };
        private static readonly int[] ForgingSynthesisTierThresholds = { 10, 12, 14 };
        private static readonly int[] ForgingRewards = { BronzeDiamonds, SilverDiamonds, SilverDiamonds, GoldDiamonds, GoldDiamonds, LegendaryDiamonds };

        // Modul: Phase - Full-Stack Production Polish, Part 2.3. Tiers II-IV
        // were previously long.MaxValue placeholders - mathematically
        // unattainable, so no player could ever cross them regardless of
        // how many harvest loops they completed. Replaced with a realistic
        // linear-then-exponential goal curve (10x growth per tier past I,
        // matching the task's own "10k, 100k, 1M" example scale).
        private static readonly long[] LogisticsThresholds = { 10_000L, 100_000L, 1_000_000L, 10_000_000L };
        private static readonly int[] LogisticsRewards = { BronzeDiamonds, SilverDiamonds, GoldDiamonds, LegendaryDiamonds };

        private static readonly long[] AlchemistThresholds = { 1L, 10L, 50L, 250L };
        private static readonly int[] AlchemistRewards = { BronzeDiamonds, SilverDiamonds, GoldDiamonds, LegendaryDiamonds };

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
            AlchemistAchievementId => "Alchemist",
            _ => $"Achievement {achievementId}",
        };

        public static string DescriptionFor(int achievementId) => achievementId switch
        {
            MonsterKillAchievementId => "Kill ten thousand monsters.",
            TreasuryAchievementId => "Spend gold, in total: from 100,000 up to 2.5 billion, in seven steps. Every sink counts - rerolls, the forge, the village, the Delve, the market.",
            ForgingAchievementId => "Fuse at the Forge 50, 200, then 500 times; then fuse an item up to rarity 10, 12, then 14.",
            AlchemistAchievementId => "Drink potions: 1, 10, 50, then 250 - from the shelf or by auto-drink, online or away.",
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
            MonsterKillAchievementId, TreasuryAchievementId, ForgingAchievementId, LogisticsAchievementId, AlchemistAchievementId,
        };

        private static readonly string[] TreasuryTierNames = { "Purse", "Pouch", "Coffer", "Strongbox", "Vault", "Treasury", "Hoard" };
        private static readonly string[] ForgingTierNames = { "Apprentice", "Smith", "Journeyman", "Master", "Artisan", "Grandmaster" };
        private static readonly string[] AlchemistTierNames = { "First Sip", "Tippler", "Alchemist", "Master Alchemist" };
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
                        tiers.Add(new LifetimeTier(TreasuryTierNames[i], $"Spend {TreasuryThresholds[i]:N0} gold in total", TreasuryRewards[i]));
                    break;
                case ForgingAchievementId:
                    for (int i = 0; i < ForgingUpgradeCountThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(ForgingTierNames[i], $"{ForgingUpgradeCountThresholds[i]:N0} fusions", ForgingRewards[i]));
                    for (int i = 0; i < ForgingSynthesisTierThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(ForgingTierNames[i + ForgingUpgradeCountThresholds.Length], $"Fuse a piece up to rarity {ForgingSynthesisTierThresholds[i]}", ForgingRewards[i + ForgingUpgradeCountThresholds.Length]));
                    break;
                case AlchemistAchievementId:
                    for (int i = 0; i < AlchemistThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(AlchemistTierNames[i], AlchemistThresholds[i] == 1 ? "Drink a potion" : $"Drink {AlchemistThresholds[i]:N0} potions", AlchemistRewards[i]));
                    break;
                case LogisticsAchievementId:
                    for (int i = 0; i < LogisticsThresholds.Length; i++)
                        tiers.Add(new LifetimeTier(LogisticsTierNames[i], $"{LogisticsThresholds[i]:N0} harvests, +{LogisticsStatBonusPctRewards[i]}% gathering speed", LogisticsRewards[i]));
                    break;
            }
            return tiers;
        }

        // Task 79: measured in gold SPENT (PlayerRecord.LifetimeGoldSpent). It
        // paid for holding gold, which rewarded hoarding in an economy that
        // already under-spends. Same thresholds; tiers already paid stay paid.
        public static int EvaluateTreasuryTier(long lifetimeGoldSpent)
        {
            int tier = 0;
            for (int i = 0; i < TreasuryThresholds.Length; i++)
            {
                if (lifetimeGoldSpent >= TreasuryThresholds[i]) tier = i + 1;
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
                if (highestSynthesisTier >= ForgingSynthesisTierThresholds[i]) tier = i + 1 + ForgingUpgradeCountThresholds.Length;
            }
            return tier;
        }

        public static int EvaluateAlchemistTier(long potionsDrunk)
        {
            int tier = 0;
            for (int i = 0; i < AlchemistThresholds.Length; i++)
            {
                if (potionsDrunk >= AlchemistThresholds[i]) tier = i + 1;
            }
            return tier;
        }

        /// <summary>
        /// The DiamondRework migration's map from a tier stored under the old
        /// four-tier ladders to the same threshold's index now (0 stays 0).
        /// Tested against the ladders above, so the SQL cannot drift from them.
        /// </summary>
        public static int MigratedTier(int achievementId, int oldTier) => achievementId switch
        {
            TreasuryAchievementId => oldTier switch { 1 => 1, 2 => 3, 3 => 5, 4 => 7, _ => oldTier },
            ForgingAchievementId => oldTier switch { 1 => 1, 2 => 3, 3 => 4, 4 => 6, _ => oldTier },
            _ => oldTier,
        };

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
                AlchemistAchievementId => AlchemistRewards,
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
                int synthesisIndex = completedTier - ForgingUpgradeCountThresholds.Length;
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

            if (achievementId == AlchemistAchievementId)
            {
                return completedTier < AlchemistThresholds.Length ? AlchemistThresholds[completedTier] : 0L;
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
