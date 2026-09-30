using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;

namespace FolkIdle.Server.Domain.Progression
{
    public sealed record GreatWorkStageView(int Stage, string Name, long Cost, bool Built);

    public sealed record GreatWorkView(
        int Region, string Name, int Stage, long Progress, long NextCost,
        string LogItem, string OreItem, long HeldLog, long HeldOre,
        string BonusPerStage, IReadOnlyList<GreatWorkStageView> Stages,
        string CompletionReward, string FrameId, bool FrameOwned);

    public sealed record GreatWorksSnapshot(
        int YieldPct, int MaxYieldPct, int OfflineMinutes, int MaxOfflineMinutes,
        int HallSlots, IReadOnlyList<GreatWorkView> Works);

    /// <summary>The tick's copy of a player's stages, after a deposit committed.</summary>
    public readonly record struct GreatWorksUpdateNotification(long PlayerId, int StagesPacked);

    /// <summary>
    /// Task 84: depositing materials into a Great Work, and reading the works back.
    ///
    /// Modul: THE DEPOSIT IS ONE TRANSACTION AND THE ROW IS LOCKED FIRST. The
    /// materials are spent through the same TryConsumeUnifiedAsync every building
    /// and recipe uses (backpack, then stash), the progress row is raised by
    /// exactly what was spent, and a stage completes inside the same commit - so
    /// a double tap, a second tab or a crash cannot leave materials gone with no
    /// progress, or progress with no materials. The amount is CLAMPED to what
    /// the current stage still needs and never carries over: a stage is built
    /// by exactly its cost, so an over-large request (or the client's "all") is
    /// a smaller deposit, not a refusal and not a lost surplus.
    ///
    /// Only AFTER the commit does the engine hand the tick the new packed stages
    /// (<see cref="Updates"/>): the payload is a cache of this table, refreshed
    /// from it, never advanced on its own.
    ///
    /// Raw SQL on snake_case tables with PascalCase quoted columns
    /// (CURRENT_IMPLEMENTATION_STATE.md section 3).
    /// </summary>
    public static class GreatWorksEngine
    {
        /// <summary>Filled after a deposit commits, drained on the tick (GreatWorksTickCoordinator).</summary>
        public static readonly ConcurrentQueue<GreatWorksUpdateNotification> Updates = new();

        /// <summary>
        /// Deposits into one monument. <paramref name="requested"/> of 0 means "as
        /// much as I hold, up to what the stage still needs". Every refusal and
        /// success is answered with a command result: the region and material come
        /// from buttons, so a refusal is a state race, never a protocol violation.
        /// </summary>
        public static async Task DepositAsync(
            IDbContextFactory<FolkIdleDbContext> factory, PlayerSessionRegistry registry,
            long playerId, int region, int materialKind, long requested)
        {
            if (!GreatWorksRegistry.IsValidRegion(region) || !GreatWorksRegistry.IsValidMaterialKind(materialKind) || requested < 0)
            {
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.GenericValidationFailure);
                return;
            }

            CommandResultCode result;
            int packed;
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                (result, packed) = await DepositCoreAsync(db, playerId, region, materialKind, requested, DateTime.UtcNow);
                if (result != CommandResultCode.GreatWorkDeposited && result != CommandResultCode.GreatWorkStageBuilt)
                {
                    registry.EnqueueCommandResult(playerId, (byte)result);
                    return;
                }
            }
            catch (Exception ex)
            {
                // Nothing was spent: the transaction rolled back with the scope.
                Console.WriteLine($"Great work deposit failed for player {playerId}: {ex.Message}");
                registry.EnqueueCommandResult(playerId, (byte)CommandResultCode.CheckpointFailed);
                return;
            }

            Updates.Enqueue(new GreatWorksUpdateNotification(playerId, packed));
            registry.EnqueueCommandResult(playerId, (byte)result);
        }

        /// <summary>The whole deposit on an open context - separate from the queueing so a test drives it directly.</summary>
        public static async Task<(CommandResultCode Result, int Packed)> DepositCoreAsync(
            FolkIdleDbContext db, long playerId, int region, int materialKind, long requested, DateTime utcNow)
        {
            var at = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            await db.Database.ExecuteSqlRawAsync(
                "INSERT INTO great_works_progress (\"PlayerId\", \"Region\", \"Stage\", \"Progress\", \"UpdatedAtUtc\") " +
                "VALUES ({0}, {1}, 0, 0, {2}) ON CONFLICT (\"PlayerId\", \"Region\") DO NOTHING",
                playerId, (short)region, at);

            var row = (await db.Database.SqlQueryRaw<GreatWorkRow>(
                "SELECT \"Stage\"::int AS \"Stage\", \"Progress\" AS \"Progress\" FROM great_works_progress " +
                "WHERE \"PlayerId\" = {0} AND \"Region\" = {1} FOR UPDATE",
                playerId, (short)region).ToListAsync()).Single();

            if (row.Stage >= GreatWorksRegistry.StageCount)
            {
                await tx.RollbackAsync();
                return (CommandResultCode.GreatWorkComplete, 0);
            }

            long cost = GreatWorksRegistry.StageCosts[row.Stage];
            long remaining = cost - row.Progress;
            string item = GreatWorksRegistry.MaterialFor(region, materialKind);

            var balance = await InventoryAndStashSystem.LockAndReadBalanceAsync(db, playerId, item);
            long amount = requested == 0
                ? Math.Min(remaining, balance.Total)
                : Math.Min(requested, remaining);

            if (amount <= 0 || balance.Total < amount
                || !await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, item, amount))
            {
                await tx.RollbackAsync();
                return (CommandResultCode.InsufficientMaterials, 0);
            }

            long progress = row.Progress + amount;
            int stage = row.Stage;
            bool built = progress >= cost;
            if (built)
            {
                stage++;
                progress = 0;
            }

            await db.SaveChangesAsync();
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE great_works_progress SET \"Stage\" = {0}, \"Progress\" = {1}, \"UpdatedAtUtc\" = {2} " +
                "WHERE \"PlayerId\" = {3} AND \"Region\" = {4}",
                (short)stage, progress, at, playerId, (short)region);

            // The completion frame, in the SAME commit as the fifth stage - a
            // monument is never complete without it. The Hall slot needs no
            // write: HallOfAncestorsRules reads it off the built stages.
            if (built && stage >= GreatWorksRegistry.StageCount)
            {
                await GrantFrameAsync(db, playerId, region, at);
            }

            await tx.CommitAsync();
            return (built ? CommandResultCode.GreatWorkStageBuilt : CommandResultCode.GreatWorkDeposited,
                await LoadPackedAsync(db, playerId));
        }

        /// <summary>
        /// Grants a completed monument's bound frame, once (the NOT EXISTS makes
        /// a repeat a no-op). The same insert the Ascension frames use.
        /// </summary>
        internal static Task GrantFrameAsync(FolkIdleDbContext db, long playerId, int region, DateTime at)
        {
            string frameId = GreatWorksRegistry.FrameId(region);
            var def = CosmeticRegistry.Find(frameId)!;
            return db.Database.ExecuteSqlRawAsync(
                "INSERT INTO cosmetic_items (\"PlayerId\", \"Kind\", \"DefinitionId\", \"Rarity\", \"Source\", \"AcquiredAtUtc\", \"IsListed\") " +
                "SELECT {0}, {1}, {2}, {3}, {4}, {5}, false WHERE NOT EXISTS " +
                "(SELECT 1 FROM cosmetic_items WHERE \"PlayerId\" = {0} AND \"DefinitionId\" = {2})",
                playerId, (short)CosmeticKind.Frame, frameId, (short)def.Rarity, (short)CosmeticSource.GreatWork, at);
        }

        /// <summary>Hall of Ancestors slots this player's Great Works pay.</summary>
        public static async Task<int> HallSlotsAsync(FolkIdleDbContext db, long playerId)
            => GreatWorksRegistry.HallSlots(await LoadPackedAsync(db, playerId));

        /// <summary>Keyless projection for the FOR UPDATE read.</summary>
        public sealed class GreatWorkRow
        {
            public int Stage { get; set; }
            public long Progress { get; set; }
        }

        /// <summary>The player's built stages, packed for the payload's cache (see GreatWorksRegistry.StageOf).</summary>
        public static async Task<int> LoadPackedAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.GreatWorkProgress.AsNoTracking()
                .Where(p => p.PlayerId == playerId)
                .Select(p => new { p.Region, p.Stage })
                .ToListAsync();
            int packed = 0;
            foreach (var row in rows) packed = GreatWorksRegistry.WithStage(packed, row.Region, row.Stage);
            return packed;
        }

        public static async Task<GreatWorksSnapshot> ViewAsync(FolkIdleDbContext db, long playerId)
        {
            var rows = await db.GreatWorkProgress.AsNoTracking()
                .Where(p => p.PlayerId == playerId)
                .ToDictionaryAsync(p => (int)p.Region);

            int packed = 0;
            foreach (var kv in rows) packed = GreatWorksRegistry.WithStage(packed, kv.Key, kv.Value.Stage);

            // What the player holds of each material, chest and backpack together
            // - the same sum TryConsumeUnifiedAsync checks.
            var items = new List<string>();
            foreach (var m in GreatWorksRegistry.Monuments)
            {
                items.Add(GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialLog));
                items.Add(GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialOre));
            }
            var held = new Dictionary<string, long>();
            foreach (var c in await db.CommodityRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && items.Contains(c.ItemId)).ToListAsync())
            {
                held[c.ItemId] = held.GetValueOrDefault(c.ItemId) + c.Quantity;
            }
            foreach (var s in await db.VillageStashInstances.AsNoTracking()
                .Where(s => s.PlayerId == playerId && items.Contains(s.ItemId)).ToListAsync())
            {
                held[s.ItemId] = held.GetValueOrDefault(s.ItemId) + s.Quantity;
            }

            var frameIds = GreatWorksRegistry.Monuments.Select(m => GreatWorksRegistry.FrameId(m.Region)).ToList();
            var ownedFrames = (await db.CosmeticItems.AsNoTracking()
                .Where(c => c.PlayerId == playerId && frameIds.Contains(c.DefinitionId))
                .Select(c => c.DefinitionId).ToListAsync()).ToHashSet();

            var works = new List<GreatWorkView>();
            foreach (var m in GreatWorksRegistry.Monuments)
            {
                rows.TryGetValue(m.Region, out var row);
                int stage = row?.Stage ?? 0;
                long progress = stage >= GreatWorksRegistry.StageCount ? 0 : row?.Progress ?? 0;
                string log = GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialLog);
                string ore = GreatWorksRegistry.MaterialFor(m.Region, GreatWorksRegistry.MaterialOre);

                var stages = new List<GreatWorkStageView>();
                for (int s = 0; s < GreatWorksRegistry.StageCount; s++)
                {
                    stages.Add(new GreatWorkStageView(s + 1, GreatWorksRegistry.StageNames[s], GreatWorksRegistry.StageCosts[s], s < stage));
                }

                works.Add(new GreatWorkView(
                    m.Region, m.Name, stage, progress,
                    stage >= GreatWorksRegistry.StageCount ? 0 : GreatWorksRegistry.StageCosts[stage],
                    log, ore, held.GetValueOrDefault(log), held.GetValueOrDefault(ore),
                    GreatWorksRegistry.DescribeStageBonus(m.Region), stages,
                    GreatWorksRegistry.DescribeCompletion(m.Region), GreatWorksRegistry.FrameId(m.Region),
                    ownedFrames.Contains(GreatWorksRegistry.FrameId(m.Region))));
            }

            return new GreatWorksSnapshot(
                GreatWorksRegistry.YieldPct(packed), GreatWorksRegistry.MaxYieldPct,
                GreatWorksRegistry.OfflineMinutes(packed), GreatWorksRegistry.MaxOfflineMinutes,
                GreatWorksRegistry.HallSlots(packed), works);
        }

        /// <summary>
        /// DEV TOOLS ONLY (exercise.mjs): sets one monument to
        /// (<paramref name="stage"/>, <paramref name="progress"/>) and moves the
        /// stock of one material by a signed <paramref name="stockDelta"/> (a
        /// positive delta is granted to the chest, a negative one is taken back if
        /// held), so a check that deposits leaves the fixture as it found it.
        /// </summary>
        public static async Task DevRestoreAsync(
            FolkIdleDbContext db, long playerId, int region, int stage, long progress, int materialKind, long stockDelta)
        {
            if (!GreatWorksRegistry.IsValidRegion(region)) return;
            stage = Math.Clamp(stage, 0, GreatWorksRegistry.StageCount);
            progress = stage >= GreatWorksRegistry.StageCount ? 0 : Math.Clamp(progress, 0, GreatWorksRegistry.StageCosts[stage] - 1);

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await db.Database.ExecuteSqlRawAsync(
                "DELETE FROM great_works_progress WHERE \"PlayerId\" = {0} AND \"Region\" = {1}", playerId, (short)region);
            if (stage > 0 || progress > 0)
            {
                await db.Database.ExecuteSqlRawAsync(
                    "INSERT INTO great_works_progress (\"PlayerId\", \"Region\", \"Stage\", \"Progress\", \"UpdatedAtUtc\") VALUES ({0}, {1}, {2}, {3}, {4})",
                    playerId, (short)region, (short)stage, progress, DateTime.UtcNow);
            }

            string item = GreatWorksRegistry.MaterialFor(region, materialKind);
            if (stockDelta > 0)
            {
                await InventoryAndStashSystem.DepositToStashAsync(db, playerId, item, stockDelta);
            }
            else if (stockDelta < 0)
            {
                var balance = await InventoryAndStashSystem.LockAndReadBalanceAsync(db, playerId, item);
                long take = Math.Min(-stockDelta, balance.Total);
                if (take > 0) await InventoryAndStashSystem.TryConsumeUnifiedAsync(db, playerId, item, take);
            }
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }
    }
}
