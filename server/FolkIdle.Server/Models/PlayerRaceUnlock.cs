using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: race unlocks. One row per race a player has earned by killing the
    // matching region boss for the first time. See RaceUnlockRegistry for the
    // boss-to-race mapping and for why the other five races were previously
    // unobtainable.
    //
    // Deliberately a table rather than a bitmask column on PlayerRecord: the
    // unlock carries a timestamp worth keeping (it is a milestone the player
    // earned, and the Codex/Statistics screens can show when), and the existing
    // PlayerRegionCompletions table already establishes exactly this
    // one-row-per-milestone shape for the same kind of progression event.
    //
    // Human is never stored here - every account starts with it, so its absence
    // from this table is not the same as being locked.
    [Table("player_race_unlocks")]
    public class PlayerRaceUnlock
    {
        [Key, Column(Order = 0)]
        public long PlayerId { get; set; }

        [Key, Column(Order = 1)]
        public int RaceId { get; set; }

        public long UnlockedAtEpoch { get; set; }

        // The boss whose first kill earned it, kept so the milestone can be
        // explained back to the player rather than just asserted.
        public int UnlockedByMonsterId { get; set; }
    }
}
