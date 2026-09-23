using System;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The tick thread's equip and unequip commands, per character.
    ///
    /// Modul: a COORDINATOR, not an engine - a static class with no fields,
    /// called synchronously on the 10Hz tick thread by SimulationEngine's
    /// command dispatch table after CommandGate has said Proceed. It owns no
    /// thread, timer or state; anything asynchronous goes through the
    /// SafeDispatch delegate it is handed, never a task it starts itself.
    /// </summary>
    internal static class EquipmentTickCoordinator
    {
        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.EquipItem)
        internal static void HandleEquipItem(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long equipPlayerId = currentPayload.PlayerId;
            long equipItemId = cmd.TargetId;
            // Modul: per-character equipment. TargetGuid names which
            // character puts the item on. Guid.Empty - what every
            // client that predates the roster sends - resolves to the
            // main character, so old behaviour is preserved exactly.
            System.Guid equipCharacterId = cmd.TargetGuid;
            if (equipItemId > 0 && ctx.EquipmentSlotEngine != null)
            {
                var equipmentSlotEngine = ctx.EquipmentSlotEngine;
                ctx.SafeDispatch("Equipment.Equip", equipPlayerId, async () => {
                    await equipmentSlotEngine.EquipItemAsync(equipPlayerId, equipItemId, equipCharacterId);
                });
            }
        }

        // Moved verbatim from EngineLoop: else if (cmd.Command == CommandType.UnequipItem)
        internal static void HandleUnequipItem(
            ref TickStatePayload currentPayload,
            ref ClientCommandPacket cmd,
            in CommandCoordinatorContext ctx)
        {
            long unequipPlayerId = currentPayload.PlayerId;
            // Modul: per-character equipment. Wire mapping widened
            // from three slots to six. TargetId now carries the slot
            // index directly (0 Weapon, 1 Helmet, 2 Chest, 3 Gloves,
            // 4 Leggings, 5 Boots).
            //
            // The one legacy case that must keep working is a client
            // that predates this and sends TargetId 0 with the old
            // IsBuy flag meaning weapon(0)/armor(1): TargetId 0 plus
            // IsBuy set is therefore read as the Chest slot, which is
            // where the old single "Armor" slot's contents now live.
            int unequipSlot = cmd.TargetId == 0L && cmd.IsBuy != 0
                ? EquipmentSlotEngine.SlotChest
                : (int)cmd.TargetId;
            System.Guid unequipCharacterId = cmd.TargetGuid;
            if (ctx.EquipmentSlotEngine != null)
            {
                var equipmentSlotEngine = ctx.EquipmentSlotEngine;
                ctx.SafeDispatch("Equipment.Unequip", unequipPlayerId, async () => {
                    await equipmentSlotEngine.UnequipItemAsync(unequipPlayerId, unequipSlot, unequipCharacterId);
                });
            }
        }
    }
}
