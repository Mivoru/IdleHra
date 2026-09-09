using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One Delve run in progress. Keyed by player, so a player has at most one
    /// - starting a second while one is live is refused rather than silently
    /// replacing it, because the first one is holding a paid entry fee.
    ///
    /// Modul: THE RUN LIVES HERE AND NOWHERE ELSE, which is the whole security
    /// design of the feature. The client sends "I choose door 2" and receives
    /// what happened; it never sends a floor, an outcome, a depth or a reward.
    /// Precedent on record: opcode 39 granted diamonds from a client field
    /// nobody signed, and a minigame is exactly the shape that invites the same
    /// mistake a second time.
    ///
    /// Deliberately NOT on TickStatePayload. The packet is near its 800-byte
    /// layout guard, a run is REST-shaped (a handful of requests minutes
    /// apart), and nothing in the 10 Hz simulation reads any of this. The one
    /// thing the tick does own is gold, which is why the entry fee is charged
    /// the way BreedingEngine charges - off the tick, against CommodityRecords,
    /// followed by a ReloadState so the live payload picks it up.
    /// </summary>
    public class DelveRunRecord
    {
        // Modul: the key is the PLAYER's id, supplied by us. Without this EF
        // reads a long primary key as generated and hangs an identity sequence
        // off it, which is a sequence nothing would ever advance and a default
        // that would quietly win if an insert ever forgot to set the id.
        [Key]
        [DatabaseGenerated(DatabaseGeneratedOption.None)]
        public long PlayerId { get; set; }

        /// <summary>What was paid to enter. Kept on the row so the consolation payout cannot drift from it.</summary>
        public long EntryFeePaid { get; set; }

        /// <summary>The floor now being offered, 1-based. FloorsCleared + 1 while a run is live.</summary>
        public int CurrentFloor { get; set; }

        public int FloorsCleared { get; set; }

        public int ChargesRemaining { get; set; }

        /// <summary>
        /// The attribute each of the three doors on the CURRENT floor demands
        /// (0 Might, 1 Finesse, 2 Vigour, 3 Fortune), packed one per byte.
        ///
        /// Packed rather than three columns because the count is a registry
        /// constant and a schema that hard-codes three would have to migrate to
        /// change it.
        /// </summary>
        public int PackedDoorDemands { get; set; }

        /// <summary>Bit i set = door i shows what it wants. The rest are the gamble - see DelveRegistry.DoorRevealChance.</summary>
        public int RevealedDoorMask { get; set; }

        public long StartedAtEpoch { get; set; }
    }
}
