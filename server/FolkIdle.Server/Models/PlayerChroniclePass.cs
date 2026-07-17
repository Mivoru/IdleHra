namespace FolkIdle.Server.Models
{
    public class PlayerChroniclePass
    {
        public long PlayerId { get; set; }
        public int PassLevel { get; set; }
        public int AccumulatedXp { get; set; }
        public ulong ClaimedMilestonesBitmask { get; set; }

        // Modul: Comprehensive Game System Audit, Part 4.3. True once the
        // player has purchased the premium track by spending
        // ChroniclePassEconomy.PremiumPassPriceDiamonds from their
        // PlayerRecord.PremiumDiamonds balance (see SimulationEngine.
        // ExecutePassPurchaseAsync) - replaces the previous placeholder
        // gating where merely HOLDING 1+ diamonds unlocked premium claims
        // without ever spending anything.
        public bool PremiumUnlocked { get; set; }
    }
}
