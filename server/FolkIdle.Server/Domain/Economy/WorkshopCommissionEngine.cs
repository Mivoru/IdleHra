using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>Every answer a commission request can get. Sent to the client by name.</summary>
    public enum CommissionResult
    {
        Ok,
        WorkshopNotBuilt,
        UnknownPiece,
        RegionLocked,
        IllegalAffix,
        Busy,
        NotEnoughMaterials,
        Restricted,
        NothingToCollect,
        NotReady,
        NotFound,
    }

    /// <summary>
    /// Task 83: placing, reading and collecting a Workshop commission. Every
    /// number comes from <see cref="WorkshopCommissionRules"/>.
    /// </summary>
    /// <remarks>
    /// Modul: REST, NOT THE TICK. A commission is a handful of requests hours
    /// apart, spends only catalogued materials (never gold, so neither of the
    /// two gold paths is involved) and writes only rows the tick never reads -
    /// CommodityRecords' material stacks, PlayerCraftingSlots and
    /// EquipmentInstances. So the handlers run on the router's per-account
    /// stripe lock like the Delve and the cosmetics, the database is the one
    /// authority, and nothing is added to StateUpdatePacket.
    ///
    /// Modul: IT FINISHES WHILE THE PLAYER IS AWAY BY CONSTRUCTION. The row
    /// holds a wall-clock CompletionEpoch; "ready" is CompletionEpoch &lt;= now,
    /// asked at read time. There is no worker to die, no offline catch-up to
    /// forget it, and no tick to run it - a player who places an 8-hour
    /// commission and logs out collects a finished piece at the next login.
    ///
    /// Every refusal is ANSWERED with a <see cref="CommissionResult"/> - never
    /// a silent rollback (CLAUDE.md).
    /// </remarks>
    public static class WorkshopCommissionEngine
    {
        public sealed record CostLineView(string ItemId, long Quantity, long Held);

        public sealed record PieceView(int ItemId, string BaseItemId, string[] Affixes);

        public sealed record RegionView(
            int Region, bool Unlocked, int FloorTier, string FloorName, long DurationSeconds,
            CostLineView[] Cost, bool Affordable, PieceView[] Pieces);

        public sealed record CommissionView(
            int ItemId, string BaseItemId, string ChosenAffixId, int FloorTier, string FloorName,
            long StartedEpoch, long CompletionEpoch, long SecondsRemaining, bool Ready);

        public sealed record WorkshopView(
            string? Result, int WorkshopLevel, int WorkshopFloorTier, int MaxWorkshopLevel,
            int HighestUnlockedRegion, long NowEpoch, RegionView[] Regions,
            CommissionView? Commission, CollectedPiece? Collected);

        public sealed record CollectedPiece(long InstanceId, string BaseItemId, int QualityTier, string RarityName, string AffixPayload);

        /// <summary>
        /// The Workshop's level as of <paramref name="nowEpoch"/>: an upgrade
        /// whose timer has run out counts even before the next command or login
        /// has written it (VillageManagementEngine.ResolveMaturedUpgradesAsync),
        /// so a GET never has to write to be right.
        /// </summary>
        public static async Task<int> ReadWorkshopLevelAsync(FolkIdleDbContext db, long playerId, long nowEpoch)
        {
            var row = await db.VillageInfrastructures.AsNoTracking()
                .Where(v => v.PlayerId == playerId && v.BuildingId == VillageManagementEngine.CraftingWorkshopBuildingId)
                .Select(v => new { v.CurrentLevel, v.UpgradeTargetLevel, v.UpgradeCompletesAtEpoch })
                .FirstOrDefaultAsync();
            if (row == null) return 0;
            bool matured = row.UpgradeTargetLevel > 0 && row.UpgradeCompletesAtEpoch <= nowEpoch;
            return matured ? Math.Max(row.CurrentLevel, row.UpgradeTargetLevel) : row.CurrentLevel;
        }

        private static async Task<Dictionary<string, long>> ReadHeldAsync(FolkIdleDbContext db, long playerId, HashSet<string> wanted)
        {
            // The two places TryConsumeUnifiedAsync spends from: the backpack
            // first, then the village stash - the same sum the village quote shows.
            var held = new Dictionary<string, long>(StringComparer.Ordinal);
            var backpack = await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && wanted.Contains(c.ItemId))
                .Select(c => new { c.ItemId, c.Quantity })
                .ToListAsync();
            var stash = await db.VillageStashInstances.AsNoTracking()
                .Where(v => v.PlayerId == playerId && wanted.Contains(v.ItemId))
                .Select(v => new { v.ItemId, v.Quantity })
                .ToListAsync();
            foreach (var row in backpack) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;
            foreach (var row in stash) held[row.ItemId] = held.GetValueOrDefault(row.ItemId) + row.Quantity;
            return held;
        }

        public static async Task<WorkshopView?> ViewAsync(
            FolkIdleDbContext db, long playerId, long nowEpoch, string? result = null, CollectedPiece? collected = null)
        {
            bool exists = await db.PlayerRecords.AsNoTracking().AnyAsync(p => p.Id == playerId);
            if (!exists) return null;

            int workshopLevel = await ReadWorkshopLevelAsync(db, playerId, nowEpoch);
            var defeated = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);
            int unlocked = RegionUnlockGate.HighestUnlockedRegion(defeated);

            var quotes = new Dictionary<int, VillageManagementEngine.UpgradeCostLine[]>();
            var wanted = new HashSet<string>(StringComparer.Ordinal);
            for (int region = WorkshopCommissionRules.FirstRegion; region <= WorkshopCommissionRules.LastRegion; region++)
            {
                int floor = WorkshopCommissionRules.FloorTierFor(workshopLevel, region);
                var lines = WorkshopCommissionRules.Quote(region, floor > 0 ? floor : RarityTier.Common);
                quotes[region] = lines;
                foreach (var line in lines) wanted.Add(line.ItemId);
            }
            var held = await ReadHeldAsync(db, playerId, wanted);

            var piecesByRegion = new Dictionary<int, List<PieceView>>();
            foreach (int itemId in WorkshopCommissionRules.CommissionableItemIds())
            {
                WorkshopCommissionRules.TryGetCommissionable(itemId, out string baseId, out int region);
                if (!piecesByRegion.TryGetValue(region, out var list)) piecesByRegion[region] = list = new List<PieceView>();
                list.Add(new PieceView(itemId, baseId, WorkshopCommissionRules.LegalAffixIds(baseId).ToArray()));
            }

            var regions = new List<RegionView>();
            for (int region = WorkshopCommissionRules.FirstRegion; region <= WorkshopCommissionRules.LastRegion; region++)
            {
                int floor = WorkshopCommissionRules.FloorTierFor(workshopLevel, region);
                var cost = quotes[region].Select(l => new CostLineView(l.ItemId, l.Quantity, held.GetValueOrDefault(l.ItemId))).ToArray();
                regions.Add(new RegionView(
                    region,
                    region <= unlocked,
                    floor,
                    floor > 0 ? RarityTier.GetName(floor) : string.Empty,
                    WorkshopCommissionRules.DurationSecondsFor(floor > 0 ? floor : RarityTier.Common),
                    cost,
                    cost.All(c => c.Held >= c.Quantity),
                    (piecesByRegion.GetValueOrDefault(region) ?? new List<PieceView>()).ToArray()));
            }

            var slot = await db.PlayerCraftingSlots.AsNoTracking()
                .FirstOrDefaultAsync(s => s.PlayerId == playerId && s.SlotIndex == WorkshopCommissionRules.CommissionSlotIndex);

            return new WorkshopView(
                result,
                workshopLevel,
                WorkshopCommissionRules.WorkshopFloor(workshopLevel),
                VillageManagementEngine.MaxStructuralBuildingLevel,
                unlocked,
                nowEpoch,
                regions.ToArray(),
                slot == null ? null : ToView(slot, nowEpoch),
                collected);
        }

        private static CommissionView ToView(PlayerCraftingSlot slot, long nowEpoch)
        {
            long remaining = Math.Max(0L, slot.CompletionEpoch - nowEpoch);
            return new CommissionView(
                slot.ActiveRecipeId,
                ContentRegistry.GetItemBaseId(slot.ActiveRecipeId),
                slot.ChosenAffixId,
                slot.FloorTier,
                RarityTier.GetName(slot.FloorTier),
                slot.StartedEpoch,
                slot.CompletionEpoch,
                remaining,
                remaining == 0L);
        }

        /// <summary>
        /// Places a commission: validates the piece, the region and the affix,
        /// charges the whole quote, and writes the row - in one transaction, so
        /// a commission that cannot be paid for costs nothing.
        /// </summary>
        public static async Task<CommissionResult> StartAsync(
            FolkIdleDbContext db, long playerId, int itemId, string? affixId, long nowEpoch)
        {
            if (!WorkshopCommissionRules.TryGetCommissionable(itemId, out string baseItemId, out int region))
            {
                return CommissionResult.UnknownPiece;
            }
            if (!WorkshopCommissionRules.IsLegalAffix(baseItemId, affixId))
            {
                return CommissionResult.IllegalAffix;
            }

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var player = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                .SingleOrDefaultAsync();
            if (player == null)
            {
                await tx.RollbackAsync();
                return CommissionResult.NotFound;
            }

            // Modul: a quarantined account has every command rejected on the
            // tick (anti-cheat); a REST route has to ask for itself, or it is
            // the one door the quarantine forgot.
            if (player.IsQuarantined || player.Quarantine_Active)
            {
                await tx.RollbackAsync();
                return CommissionResult.Restricted;
            }

            int workshopLevel = await ReadWorkshopLevelAsync(db, playerId, nowEpoch);
            int floor = WorkshopCommissionRules.FloorTierFor(workshopLevel, region);
            if (floor <= 0)
            {
                await tx.RollbackAsync();
                return CommissionResult.WorkshopNotBuilt;
            }

            // The same gate that decides whether the piece may be WORN: a
            // commission of a region you cannot wear would be a piece for the
            // chest and nothing else.
            var defeated = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);
            if (!RegionUnlockGate.CanWearRegionTier(region, defeated))
            {
                await tx.RollbackAsync();
                return CommissionResult.RegionLocked;
            }

            bool busy = await db.PlayerCraftingSlots
                .AnyAsync(s => s.PlayerId == playerId && s.SlotIndex == WorkshopCommissionRules.CommissionSlotIndex);
            if (busy)
            {
                await tx.RollbackAsync();
                return CommissionResult.Busy;
            }

            foreach (var line in WorkshopCommissionRules.Quote(region, floor))
            {
                if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, line.ItemId, line.Quantity))
                {
                    await tx.RollbackAsync();
                    db.ChangeTracker.Clear();
                    return CommissionResult.NotEnoughMaterials;
                }
            }

            db.PlayerCraftingSlots.Add(new PlayerCraftingSlot
            {
                PlayerId = playerId,
                SlotIndex = WorkshopCommissionRules.CommissionSlotIndex,
                ActiveRecipeId = itemId,
                ChosenAffixId = affixId!,
                FloorTier = floor,
                StartedEpoch = nowEpoch,
                CompletionEpoch = nowEpoch + WorkshopCommissionRules.DurationSecondsFor(floor),
                IsReady = false,
            });

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return CommissionResult.Ok;
        }

        /// <summary>
        /// Hands over a finished commission: the rarity rolls above its floor,
        /// the chosen affix goes on at Common, the rest roll like a drop's, and
        /// the piece lands in the chest. The row is deleted in the same
        /// transaction, so a double tap collects once.
        /// </summary>
        public static async Task<(CommissionResult Result, CollectedPiece? Piece)> CollectAsync(
            FolkIdleDbContext db, long playerId, long nowEpoch, Random rng)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var slot = await db.PlayerCraftingSlots
                .FromSqlRaw(
                    "SELECT * FROM \"PlayerCraftingSlots\" WHERE \"PlayerId\" = {0} AND \"SlotIndex\" = {1} FOR UPDATE",
                    playerId, (short)WorkshopCommissionRules.CommissionSlotIndex)
                .SingleOrDefaultAsync();
            if (slot == null)
            {
                await tx.RollbackAsync();
                return (CommissionResult.NothingToCollect, null);
            }
            if (slot.CompletionEpoch > nowEpoch)
            {
                await tx.RollbackAsync();
                return (CommissionResult.NotReady, null);
            }

            // Modul: A PAID ORDER IS NEVER DELETED FOR NOTHING. If the piece
            // stopped being commissionable between placing and collecting (a
            // content change), the refusal is answered and the row is KEPT, so
            // a human can see what was paid for, rather than the row vanishing
            // with nothing to show for the materials.
            if (!WorkshopCommissionRules.TryGetCommissionable(slot.ActiveRecipeId, out string baseItemId, out int region))
            {
                await tx.RollbackAsync();
                return (CommissionResult.UnknownPiece, null);
            }

            db.PlayerCraftingSlots.Remove(slot);

            int tier = WorkshopCommissionRules.ResolveTier(slot.FloorTier, rng);
            var affixes = WorkshopCommissionRules.BuildAffixes(baseItemId, region, tier, slot.ChosenAffixId);

            var instance = new EquipmentInstance
            {
                BaseItemId = baseItemId,
                PlayerId = playerId,
                QualityTier = tier,
                AffixPayload = JsonSerializer.Serialize(affixes),
                IsAffixLocked = false,
            };
            db.EquipmentInstances.Add(instance);

            // The drop record counts crafted pieces too (task 26); a commission
            // is a craft, and unlike the bench's it is not always Normal.
            var tally = new DropTally();
            tally.Count(DropSource.Craft, region, tier);
            await DropRecord.WriteAsync(db, playerId, tally, DateTime.UtcNow);

            await db.SaveChangesAsync();
            await tx.CommitAsync();

            return (CommissionResult.Ok, new CollectedPiece(instance.Id, baseItemId, tier, RarityTier.GetName(tier), instance.AffixPayload));
        }

        /// <summary>
        /// Dev tools only (FOLKIDLE_DEV_TOOLS=1): finish the running commission
        /// now, and optionally give back what it cost - so exercise.mjs can
        /// place, collect and discard one without spending the fixture's stock
        /// for good or waiting hours.
        /// </summary>
        public static async Task<CommissionResult> DevFinishAsync(FolkIdleDbContext db, long playerId, long nowEpoch, bool refund)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            var slot = await db.PlayerCraftingSlots
                .FirstOrDefaultAsync(s => s.PlayerId == playerId && s.SlotIndex == WorkshopCommissionRules.CommissionSlotIndex);
            if (slot == null)
            {
                await tx.RollbackAsync();
                return CommissionResult.NothingToCollect;
            }

            slot.CompletionEpoch = nowEpoch;
            if (refund && WorkshopCommissionRules.TryGetCommissionable(slot.ActiveRecipeId, out _, out int region))
            {
                foreach (var line in WorkshopCommissionRules.Quote(region, slot.FloorTier))
                {
                    await CommodityLedger.AddAsync(db, playerId, line.ItemId, line.Quantity);
                }
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return CommissionResult.Ok;
        }
    }
}
