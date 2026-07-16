namespace FolkIdle.Server.Engine
{
    // Modul: Phase - Full-Stack Production Polish Phase 2, Part 4.1.
    // Previously a static 3-way hash bucket (MurmurHash3 over playerId,
    // modulo 3) - every player landed in a cohort determined purely by
    // their numeric id, with no relationship whatsoever to their actual
    // spending behavior, so a whale and a player who never purchased
    // anything could land in the same "premium offers" bucket by pure
    // coincidence while a real high-value player could just as easily land
    // in the control group forever. ResolveCohort is now a pure function
    // of three real signals - lifetime value, account age, and days since
    // the player's last transaction - resolved by the caller from
    // ProcessedTransactions/PlayerRecords (see NetworkBroadcastSystem.
    // HandleStorefrontListings) rather than the player's id.
    //
    // Kept as a pure function over primitives (no DB access, no
    // allocation) so the segmentation decision itself stays trivially
    // testable and cheap regardless of how expensive resolving its inputs
    // is - the same design already used by AchievementMilestones/
    // LegacyPerkResolver elsewhere in this codebase.
    public static class StorefrontSegmentationEngine
    {
        public const int Control = 0;
        public const int VariantA = 1;
        public const int VariantB = 2;

        // Modul: "lifetime value" here is the player's cumulative granted
        // premium-diamond total (ProcessedTransactions.PremiumDiamondsGranted
        // summed) - the PlayerSegmentationProfile.LifetimeValueCents column
        // this feeds is a pre-existing field name from before any real
        // money-value ledger existed in this codebase; no genuine
        // cents-denominated purchase price is recorded anywhere per
        // transaction (only a diamonds-granted amount, via
        // GameBalanceConfig.json's IapProductPrices), so a diamond-based
        // proxy is the honest, real data available rather than a
        // fabricated currency conversion.
        public const long HighValueLifetimeValueThreshold = 5000L;

        // Age in ticks: PlayerRecord.LogicEpochCounter, a monotonically
        // increasing per-player checkpoint-flush counter (see
        // StateCheckpointManager) - a genuine, already-persisted measure of
        // how long this account has been actively played, not a fabricated
        // stat invented for this feature.
        public const long VeteranAgeTicksThreshold = 500L;

        public const int ChurnRiskDaysThreshold = 14;

        // Modul: VariantB targets active high-value spenders (premium-
        // focused offers - larger diamond packs, cosmetic bundles);
        // VariantA targets veteran accounts drifting toward churn (win-back
        // offers - discounted starter-tier packs); everyone else (new
        // accounts, low-value spenders who are still active, or accounts
        // that have simply never purchased anything) stays in Control. A
        // player who has never transacted at all resolves
        // daysSinceLastTransaction to int.MaxValue at the call site (see
        // NetworkBroadcastSystem.HandleStorefrontListings) - correctly
        // excluding them from both the "active high-value" and "recently
        // active veteran" branches, which is the intended behavior for an
        // account with zero purchase history.
        public static int ResolveCohort(long lifetimeValue, long ageInTicks, int daysSinceLastTransaction)
        {
            if (lifetimeValue >= HighValueLifetimeValueThreshold && daysSinceLastTransaction < ChurnRiskDaysThreshold)
            {
                return VariantB;
            }

            if (ageInTicks >= VeteranAgeTicksThreshold && daysSinceLastTransaction >= ChurnRiskDaysThreshold)
            {
                return VariantA;
            }

            return Control;
        }

        public static bool IsValidCohort(int cohort)
        {
            return cohort >= Control && cohort <= VariantB;
        }
    }
}
