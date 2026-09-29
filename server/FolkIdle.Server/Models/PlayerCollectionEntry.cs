using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// THE COLLECTION LOG (task 57): for every catalogued piece, the highest
    /// rarity this account has ever owned.
    ///
    /// "Ever owned", not "owns": the Chest is sold and fused away constantly,
    /// so reading EquipmentInstances would forget a Mythic the moment it was
    /// melted into something better. Two writers, both only ever RAISE
    /// BestTier (an upsert with GREATEST): the loot worker, after its commit,
    /// for every piece it drops; and the collection read, which folds in
    /// whatever the Chest holds now - that is what catches crafted, fused and
    /// market-bought pieces without a third writer on each of those paths.
    ///
    /// Snake_case table, PascalCase quoted columns, composite key
    /// (PlayerId, BaseItemId) in FolkIdleDbContext. No foreign key, like
    /// player_funnel_events: a batch must not fail because an account went.
    /// </summary>
    [Table("player_collection")]
    public class PlayerCollectionEntry
    {
        public long PlayerId { get; set; }

        [MaxLength(128)]
        public string BaseItemId { get; set; } = string.Empty;

        public int BestTier { get; set; }

        /// <summary>When the first copy of this piece was recorded, UTC.</summary>
        public DateTime FirstOwnedAtUtc { get; set; }
    }
}
