namespace FolkIdle.Server.Models
{
    public class PlayerRecord
    {
        public long Id { get; set; }
        public int CurrentLevel { get; set; }
        public long CurrentXp { get; set; }
        public int SelectedLineageId { get; set; }
        public System.Guid PlayerGuid { get; set; }
        public System.Guid AuthenticatorToken { get; set; }
        public long LastLogoutTimestamp { get; set; }
        public int AccumulatedTimeBankSeconds { get; set; }
        public long GuildId { get; set; }
        public int ActiveOffensivePotionId { get; set; }
        public int OffensivePotionDurationMs { get; set; }
        public int ActiveDefensivePotionId { get; set; }
        public int DefensivePotionDurationMs { get; set; }
        public int PremiumDiamonds { get; set; }
        public bool Quarantine_Active { get; set; }
        public bool IsQuarantined { get; set; }
        public long LogicEpochCounter { get; set; }
        public double BankedChronoSeconds { get; set; }
        public bool IsChronoAccelerating { get; set; }
    }
}
