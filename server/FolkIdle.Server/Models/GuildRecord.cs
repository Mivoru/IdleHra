using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildRecord
    {
        [Key]
        public long Id { get; set; }
        
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        
        public long TotalGoldContributed { get; set; }
        
        public int CurrentTier { get; set; }

        public int MiningMonolithLevel { get; set; }
        public int MiningMonolithProgress { get; set; }
        public int WoodcuttingMonolithLevel { get; set; }
        public int WoodcuttingMonolithProgress { get; set; }
        public int GuildMMR { get; set; } = 1000;
        public int MaxMembers { get; set; } = 10;
        public int ActiveMembers { get; set; } = 1;

        // Modul: Advanced Economy Refactoring, Part 2.4. Guild sales tax
        // rate in whole percent, applied to the seller's gross market
        // price on every completed sale (see MarketEscrowEngine.
        // BuyItemAsync) and deposited into the guild's gold ledger row.
        // Clamped to [MinTaxRatePct, MaxTaxRatePct] at every write AND
        // defensively at every read, editable only by the guild Leader
        // (GuildManagementEngine.SetGuildTaxRateAsync).
        public int TaxRatePct { get; set; } = 5;
        public const int MinTaxRatePct = 5;
        public const int MaxTaxRatePct = 20;

        // Modul: Advanced Economy Refactoring, Part 3.2. Access control -
        // 0 = Open (auto-join for anyone meeting the level gates),
        // 1 = Application Required (join requests land in
        // GuildApplications for manual approval instead of joining
        // immediately).
        public int JoinType { get; set; }

        // Minimum player level to join or apply, editable by the guild
        // Leader; never below the universal structural gate
        // (GuildManagementEngine.MinGuildInteractionLevel = 20).
        public int MinApplicationLevel { get; set; } = 20;
    }
}
