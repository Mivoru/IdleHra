using System;

namespace FolkIdle.Server.Models
{
    /// <summary>
    /// One unopened loot chest (CHESTS_DUNGEONS_DIAMONDS.md section 1). A row
    /// per chest, so opening consumes the row in the same transaction that
    /// writes its contents: a double tap or a replay finds no row and grants
    /// nothing. Written only by LootChestEngine.
    /// </summary>
    public class LootChest
    {
        public long Id { get; set; }
        public long PlayerId { get; set; }

        /// <summary>LootChestTier: 1 Bronze, 2 Silver, 3 Gold, 4 Ancient.</summary>
        public byte Tier { get; set; }

        /// <summary>The region the chest was earned in; its materials and equipment come from here.</summary>
        public byte Region { get; set; }

        /// <summary>LootChestSource, for the drop record and for measuring which source pays what.</summary>
        public byte Source { get; set; }

        public DateTime GrantedAtUtc { get; set; }
    }

    /// <summary>
    /// The chests' own weekly diamond ceiling. Its own table rather than
    /// columns on PlayerRecord, so the tick's checkpoint - which writes that
    /// row every few seconds - can never write over it: one writer.
    /// </summary>
    public class LootChestWeek
    {
        public long PlayerId { get; set; }
        public int WeekKey { get; set; }
        public int DiamondsThisWeek { get; set; }
    }
}
