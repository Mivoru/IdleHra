using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Modul: Loot Event Feed. Server to client, one message per individual
    // item actually granted by a combat kill.
    //
    // Why a dedicated packet rather than fields on StateUpdatePacket: a
    // single kill can grant several items at once (CombatLootEngine rolls
    // materials plus four equipment categories plus a guaranteed boss armor
    // piece, all independently), and StateUpdatePacket is a fixed-size
    // snapshot with no room for a variable-length list - it is also already
    // at 699 of its documented 700-byte ceiling. Drops are also bursty and
    // rare rather than per-tick, so carrying them on the 10Hz snapshot
    // channel would waste bandwidth on every tick to describe an event that
    // happens a few times a minute.
    //
    // Fixed size and unmanaged by construction, so both the send and receive
    // paths are a single blittable write/read with no allocation - see
    // NetworkPacketLayoutGuard for why the exact byte count matters (both
    // sides' receive loops distinguish message types by size alone).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ResponseLootDropPacket
    {
        // Drop classifications. Materials stack into CommodityRecords;
        // equipment becomes an EquipmentInstance row; scrap is what an
        // equipment roll degrades into when the backpack is full
        // (CombatLootEngine deposits regional raw material to the stash
        // instead of silently discarding the drop).
        public const byte DropKindMaterial = 0;
        public const byte DropKindEquipment = 1;
        public const byte DropKindScrap = 2;

        public long PlayerId;

        // ContentRegistry item id - the client resolves it to a display name
        // and sprite through its own mirror of the same content files, so no
        // string ever goes over this wire.
        public int ItemId;

        public int Quantity;

        // Which monster produced the drop, so the client can attribute it.
        public int MonsterId;

        // RarityTier value 1-14 for equipment; 0 for materials and scrap,
        // which have no rarity roll.
        public byte QualityTier;

        public byte DropKind;
    }
}
