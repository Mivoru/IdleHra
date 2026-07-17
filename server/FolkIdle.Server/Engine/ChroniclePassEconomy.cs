namespace FolkIdle.Server.Engine
{
    // Modul: Comprehensive Game System Audit, Part 4.2/4.3. Single source
    // of truth for the Chronicle Pass premium track's currency economics -
    // previously no purchase flow existed at all: the "premium track" was
    // unlocked by merely holding 1+ PremiumDiamonds (never spending any),
    // granted zero diamonds across all 50 tiers, and the
    // EventHorizonPremiumLedger rows it wrote recorded no balance movement.
    //
    // The self-sustaining loop implemented here mirrors the standard
    // battle-pass economy: the pass costs PremiumPassPriceDiamonds once,
    // and a player who claims every premium milestone earns
    // TotalPremiumDiamondRewards back - strictly more than the purchase
    // price - so a fully active player's season rewards always cover the
    // next season's pass. Reward placement: every 5th milestone (5, 10,
    // 15, ..., 50) grants 100 diamonds; 10 payouts * 100 = 1000 total
    // against a 950 price, a +50 diamond dividend per completed season.
    //
    // All methods are pure integer arithmetic - no allocation, safe to
    // call from any thread including the tick loop.
    public static class ChroniclePassEconomy
    {
        public const int PremiumPassPriceDiamonds = 950;
        public const int MilestoneCount = 50;
        public const int DiamondRewardInterval = 5;
        public const int DiamondRewardPerPayout = 100;

        // Diamonds granted by the PREMIUM track at the given zero-based
        // milestone index (0-49). Milestones whose 1-based number is a
        // multiple of DiamondRewardInterval pay out; all others grant
        // equipment only.
        public static int GetPremiumDiamondReward(int milestoneIndex)
        {
            if (milestoneIndex < 0 || milestoneIndex >= MilestoneCount)
            {
                return 0;
            }

            return (milestoneIndex + 1) % DiamondRewardInterval == 0 ? DiamondRewardPerPayout : 0;
        }

        public static int TotalPremiumDiamondRewards()
        {
            int total = 0;
            for (int i = 0; i < MilestoneCount; i++)
            {
                total += GetPremiumDiamondReward(i);
            }
            return total;
        }
    }
}
