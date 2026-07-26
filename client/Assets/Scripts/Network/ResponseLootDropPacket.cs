using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Modul: Loot Event Feed. Server to client, one message per individual
    // item actually granted by a combat kill. Mirrors the server struct of
    // the same name exactly - see it for why drops ride their own packet
    // rather than fields on StateUpdatePacket.
    //
    // WebSocketClient's receive loop recognizes this by its exact byte size
    // (22), distinct from every other packet on this wire; see
    // NetworkPacketLayoutGuard.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ResponseLootDropPacket
    {
        public const byte DropKindMaterial = 0;
        public const byte DropKindEquipment = 1;
        public const byte DropKindScrap = 2;

        public long PlayerId;

        // ContentRegistry item id, resolved to a display name locally
        // through ClientContentRegistry - no string crosses this wire.
        public int ItemId;

        public int Quantity;
        public int MonsterId;

        // RarityTier 1-14 for equipment; 0 for materials and scrap.
        public byte QualityTier;

        public byte DropKind;
    }
}
