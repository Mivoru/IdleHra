namespace FolkIdle.Server.Models
{
    // Tracks which ActiveSkillEngine skill IDs a player has permanently
    // unlocked. Composite key (PlayerId, SkillId), matching the
    // MonsterCodexEntry/PlayerRaceMastery join-table convention. Hydrated
    // once at login into TickStatePayload.UnlockedSkillsBitmask (see
    // StateCheckpointManager.LoadPlayerState) - never queried from the
    // 10 Hz hot loop.
    public class PlayerSkillUnlock
    {
        public long PlayerId { get; set; }

        public int SkillId { get; set; }

        public long UnlockedAtEpoch { get; set; }
    }
}
