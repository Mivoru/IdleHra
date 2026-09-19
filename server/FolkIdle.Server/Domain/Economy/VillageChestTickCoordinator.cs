using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// The tick thread's three village-chest hand-offs. Grouped in one
    /// coordinator because the auto-salvage and chest-sale drains are a
    /// matched pair - see AutoSalvageNotification's and
    /// ChestSaleGoldNotification's own comments - and splitting them into two
    /// tasks or two coordinators would separate the two halves of one
    /// explanation. ChestSettingsQueue rides along because it is the same
    /// domain and the same coordinator, not because it shares state.
    /// </summary>
    internal static class VillageChestTickCoordinator
    {
        // Modul: auto-salvage proceeds. The same AddGold /
        // RedisPendingGoldDelta / RequiresRedisFlush triple a combat
        // kill's own gold reward uses - see the goldReward block in
        // ProcessSubTick. Deliberately NOT credited to
        // CommodityRecords by the loot worker: this session holds
        // unbanked gold in the payload and the checkpoint applies it as
        // an increment, so writing the row directly as well would pay
        // the player twice the moment the next checkpoint landed.
        //
        // An offline player has no payload and cannot be salvaging -
        // the drop that produced this was rolled for a kill their
        // session made - but the null-ref guard stays for the window
        // where they log out between the roll and this drain. Dropping
        // the gold there loses a few coins; the alternative is a second
        // write path that can disagree with the checkpoint.
        internal static void DrainAutoSalvage(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.AutoSalvageQueue.TryDequeue(out var salvage))
            {
                ref var salvagePayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, salvage.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref salvagePayload))
                {
                    continue;
                }

                ApplyAutoSalvage(ref salvagePayload, in salvage);
            }
        }

        internal static void ApplyAutoSalvage(ref TickStatePayload salvagePayload, in AutoSalvageNotification salvage)
        {
            salvagePayload.AddGold(salvage.GoldGained);
            salvagePayload.RedisPendingGoldDelta += salvage.GoldGained;
            salvagePayload.RequiresRedisFlush = true;
            salvagePayload.IsDirty = true;
        }

        // Modul: a chest sale's proceeds. READ ChestSaleGoldNotification
        // BEFORE EDITING THIS - it is deliberately not the drain above.
        //
        // VillageChestEngine already wrote CommodityRecords["gold"] in
        // its own transaction, so the row is correct and only the
        // session's displayed total is behind. AddGold fixes that.
        // RedisPendingGoldDelta is left alone on purpose: the checkpoint
        // applies it as an INCREMENT to the row the engine just
        // credited, so banking it here would pay the sale twice, one
        // checkpoint later, where nothing would connect the two.
        internal static void DrainChestSaleGold(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.ChestSaleGoldQueue.TryDequeue(out var chestSale))
            {
                ref var chestSalePayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, chestSale.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref chestSalePayload))
                {
                    continue;
                }

                ApplyChestSaleGold(ref chestSalePayload, in chestSale);
            }
        }

        internal static void ApplyChestSaleGold(ref TickStatePayload chestSalePayload, in ChestSaleGoldNotification chestSale)
        {
            chestSalePayload.AddGold(chestSale.GoldGained);
            chestSalePayload.IsDirty = true;
        }

        // Modul: an auto-salvage threshold the player just changed. The
        // loot engine reads this off the payload, which is hydrated at
        // login - without this drain the setting would not bite until
        // the next sign-in and would read as a toggle that does nothing.
        internal static void DrainChestSettings(
            PlayerSessionRegistry registry,
            Dictionary<long, TickStatePayload> activePlayers)
        {
            while (registry.ChestSettingsQueue.TryDequeue(out var chestSettings))
            {
                ref var chestSettingsPayload = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(activePlayers, chestSettings.PlayerId);
                if (System.Runtime.CompilerServices.Unsafe.IsNullRef(ref chestSettingsPayload))
                {
                    continue;
                }

                ApplyChestSettings(ref chestSettingsPayload, in chestSettings);
            }
        }

        internal static void ApplyChestSettings(ref TickStatePayload chestSettingsPayload, in ChestSettingsNotification chestSettings)
        {
            chestSettingsPayload.AutoSalvageBelowTier = chestSettings.AutoSalvageBelowTier;
            chestSettingsPayload.IsDirty = true;
        }
    }
}
