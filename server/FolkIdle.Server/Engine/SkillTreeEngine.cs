using System;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Spends skill points on the tree.
    ///
    /// Points are earned one per ACCOUNT level and live on
    /// PlayerRecords."AvailableSkillPoints" - the same column the four active
    /// skills used, kept because the points were the good part of that system.
    /// What they buy is passive now; see SkillTreeRegistry.
    /// </summary>
    public sealed class SkillTreeEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public SkillTreeEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public static async Task<byte[]> LoadLevelsAsync(FolkIdleDbContext db, long playerId)
        {
            var levels = new byte[SkillTreeRegistry.NodeCount];

            var rows = await db.PlayerSkillTreeNodes
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();

            for (int i = 0; i < rows.Count; i++)
            {
                int id = rows[i].BranchId;
                if (!SkillTreeRegistry.IsValidNode(id)) continue;
                levels[id] = (byte)Math.Clamp(rows[i].Level, 0, SkillTreeRegistry.MaxLevelOf(id));
            }

            return levels;
        }

        /// <summary>
        /// Gives back what a player overpaid when the root cap moved from 20 to
        /// 10.
        ///
        /// The three-ring tree needs roots to be the cheap layer, so their cap
        /// halved and the ceiling moved into the boughs and crowns above. A
        /// player who had already bought level 14 of Fortune paid for four
        /// levels that no longer exist, and silently deleting them would be
        /// taking points a player earned.
        ///
        /// NATURALLY IDEMPOTENT: it only touches rows above the cap, and it
        /// leaves them at the cap - so the second run finds nothing. That is
        /// why this can sit on the load path instead of being a migration that
        /// has to be remembered.
        /// </summary>
        public static async Task<int> ReconcileRootCapAsync(FolkIdleDbContext db, long playerId)
        {
            var overCap = await db.PlayerSkillTreeNodes
                .Where(r => r.PlayerId == playerId
                         && r.BranchId < SkillTreeRegistry.RootCount
                         && r.Level > SkillTreeRegistry.RootMaxLevel)
                .ToListAsync();

            if (overCap.Count == 0) return 0;

            int refund = 0;
            foreach (var row in overCap)
            {
                // What the levels above the new cap cost under the OLD curve,
                // which is the curve they were actually bought at.
                for (int level = SkillTreeRegistry.RootMaxLevel; level < row.Level; level++)
                {
                    refund += (level / 5) + 1;
                }
                row.Level = SkillTreeRegistry.RootMaxLevel;
            }

            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player != null) player.AvailableSkillPoints += refund;

            await db.SaveChangesAsync();
            return refund;
        }

        /// <summary>
        /// Why a respec cannot happen right now, or null if it can.
        ///
        /// One free per season, then one per purchased grant. Anything else
        /// would either strand a player behind a misclick for ninety days, or
        /// - if it were free and unlimited - delete ring 2's exclusivity,
        /// which is the only real choice the tree has.
        /// </summary>
        public static string? RespecBlockedReason(bool freeRespecUsed, int paidGrants)
        {
            if (!freeRespecUsed) return null;
            if (paidGrants > 0) return null;
            return "You have used this run's free respec - a rebirth brings it back.";
        }

        /// <summary>The War and Hearth halves' ranks, one byte per rank-buffer slot; old-tree rows are ignored.</summary>
        public static async Task<byte[]> LoadRanksAsync(FolkIdleDbContext db, long playerId)
        {
            var ranks = new byte[WarTreeRegistry.Slots];
            var rows = await db.PlayerSkillTreeNodes
                .AsNoTracking()
                .Where(r => r.PlayerId == playerId)
                .ToListAsync();
            foreach (var row in rows)
            {
                if (WarTreeRegistry.Get(row.BranchId) is { } n)
                    ranks[row.BranchId] = (byte)Math.Clamp(row.Level, 0, n.MaxLevel);
                else if (HearthTreeRegistry.Get(row.BranchId) is { } h)
                    ranks[row.BranchId] = (byte)Math.Clamp(row.Level, 0, h.MaxLevel);
            }
            return ranks;
        }

        /// <summary>
        /// Refunds every point a player spent on today's tree (ids 0-19), deletes
        /// those rows, and grants a free respec. Returns the points handed back.
        /// </summary>
        /// <remarks>
        /// Modul: THE SWITCH TO THE WAR HALF (rework slice 2.5), ON THE LOAD PATH,
        /// NOT IN A MIGRATION. The refund is the old cost curve
        /// (SkillTreeRegistry.GetUpgradeCost), which is C#, not SQL - and a
        /// player who logs in a month later is refunded exactly as one who
        /// logged in today. NATURALLY IDEMPOTENT: the rows it reads are the
        /// rows it deletes, so the second run finds nothing.
        ///
        /// It MUST run before hydration reads the PlayerRecords row, on the
        /// same context: the checkpoint flush writes AvailableSkillPoints back
        /// from the payload, so a payload built from a stale row would write
        /// the refund away again at the next flush.
        ///
        /// Root rows above today's cap of 10 were bought under the old cap of
        /// 20 and never refunded (ReconcileRootCapAsync was never wired); those
        /// levels are refunded here too, at the curve they were bought at.
        /// </remarks>
        public static async Task<int> ReconcileWarSwitchAsync(FolkIdleDbContext db, long playerId)
        {
            var old = await db.PlayerSkillTreeNodes
                .Where(r => r.PlayerId == playerId && r.BranchId < WarTreeRegistry.FirstId)
                .ToListAsync();
            if (old.Count == 0) return 0;

            int refund = LegacyRefundOf(old.Select(r => (r.BranchId, r.Level)));
            db.PlayerSkillTreeNodes.RemoveRange(old);
            var player = await db.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player != null)
            {
                player.AvailableSkillPoints += refund;
                // Everyone's tree changed under them, so everyone who had one
                // gets a free choice of the new one.
                player.FreeRespecUsed = false;
            }
            await db.SaveChangesAsync();
            return refund;
        }

        /// <summary>What rows of today's tree cost: the curve per level, plus the old root levels above the cap of 10.</summary>
        internal static int LegacyRefundOf(System.Collections.Generic.IEnumerable<(int BranchId, int Level)> rows)
        {
            int refund = 0;
            foreach (var (id, level) in rows)
            {
                if (!SkillTreeRegistry.IsValidNode(id)) continue;
                int capped = Math.Clamp(level, 0, SkillTreeRegistry.MaxLevelOf(id));
                for (int l = 0; l < capped; l++) refund += SkillTreeRegistry.GetUpgradeCost(id, l);
                if (id < SkillTreeRegistry.RootCount)
                    for (int l = SkillTreeRegistry.RootMaxLevel; l < level; l++) refund += (l / 5) + 1;
            }
            return refund;
        }

        /// <summary>
        /// Why a respec cannot happen right now, or null: the free one, a paid
        /// grant, or (when asked) gold at WarTreeRegistry.RespecGoldPrice.
        /// </summary>
        public static string? RespecBlockedReason(bool freeRespecUsed, int paidGrants, bool payWithGold, long gold, long goldPrice)
        {
            if (!payWithGold) return RespecBlockedReason(freeRespecUsed, paidGrants);
            if (goldPrice <= 0) return "Nothing to refund.";
            if (gold < goldPrice) return $"A respec costs {goldPrice:N0} gold; you have {gold:N0}.";
            return null;
        }

        /// <summary>
        /// Refunds every point in the tree and clears every node.
        ///
        /// ALL OR NOTHING, deliberately. A partial respec would need a rule
        /// for what happens to a keystone whose gate was just refunded out
        /// from under it, and "all of it" has no such corner.
        ///
        /// The refund is recomputed from the costs rather than stored: a
        /// "points spent" column would be a second source of truth for
        /// something derivable.
        ///
        /// payWithGold: spend gold (D3) instead of the free respec or a grant.
        /// The gold row is debited in the same transaction, and the live
        /// payload follows through the sync queue - the village's pattern.
        /// </summary>
        public async Task<SkillTreeRespecResult> RespecAsync(long playerId, bool payWithGold = false)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var result = new SkillTreeRespecResult { Reason = "No such player." };

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();
                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeRespecResult { Reason = "No such player." };
                        return;
                    }

                    // Modul: THE WAR RESPEC LEAVES THE HEARTH ROWS ALONE (2026-10-30).
                    // This deleted every row the player had, which was right while
                    // the War half was the only tree; Hearth is paid with craft
                    // points, not skill points, so refunding it here would hand
                    // skill points for craft points and wipe a build nobody asked
                    // to reset.
                    var rows = (await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync())
                        .Where(r => HearthTreeRegistry.Get(r.BranchId) == null)
                        .ToList();

                    var ranks = new byte[WarTreeRegistry.Slots];
                    foreach (var row in rows)
                        if (WarTreeRegistry.Get(row.BranchId) is { } n) ranks[row.BranchId] = (byte)Math.Clamp(row.Level, 0, n.MaxLevel);
                    int total = WarTreeRegistry.PointsSpent(ranks)
                        + LegacyRefundOf(rows.Where(r => r.BranchId < WarTreeRegistry.FirstId).Select(r => (r.BranchId, r.Level)));

                    var gold = payWithGold
                        ? await db.CommodityRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold")
                        : null;
                    long price = payWithGold ? WarTreeRegistry.RespecGoldPrice(total, player.CurrentLevel) : 0L;
                    string? refusal = total <= 0
                        ? "Nothing to refund."
                        : RespecBlockedReason(player.FreeRespecUsed, player.PaidRespecGrants, payWithGold, gold?.Quantity ?? 0L, price);
                    if (refusal != null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeRespecResult { Reason = refusal };
                        return;
                    }

                    db.PlayerSkillTreeNodes.RemoveRange(rows);
                    player.AvailableSkillPoints += total;
                    if (payWithGold)
                    {
                        gold!.Quantity -= price;
                        await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Respec, price);
                    }
                    // The free one first, so a player who bought a grant still
                    // has it after their free respec of the run.
                    else if (!player.FreeRespecUsed) player.FreeRespecUsed = true;
                    else player.PaidRespecGrants--;

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    result = new SkillTreeRespecResult { Refunded = total, GoldSpent = price };

                    // The tick thread owns the payload, so every cleared node
                    // goes back through the queue - the same route a purchase
                    // takes. The gold rides the first notification only.
                    bool first = true;
                    foreach (var row in rows)
                    {
                        _playerRegistry?.SkillTreeSyncQueue.Enqueue(new SkillTreeSyncNotification
                        {
                            PlayerId = playerId,
                            BranchId = row.BranchId,
                            NewLevel = 0,
                            RemainingSkillPoints = player.AvailableSkillPoints,
                            FreeRespecUsed = (byte)(player.FreeRespecUsed ? 1 : 0),
                            PaidRespecGrants = (byte)Math.Clamp(player.PaidRespecGrants, 0, 255),
                            GoldSpent = first ? price : 0L,
                        });
                        first = false;
                    }
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });

            return result;
        }

        /// <summary>
        /// Buys a whole drafted tree at once: every rank in it, or none.
        /// </summary>
        /// <remarks>
        /// Modul: the screen's draft mode, on the War half since slice 2.5. The
        /// check is WarTreeRegistry.PlanReason, the same one the preview runs,
        /// under row locks - a draft judged legal against a stale balance is
        /// judged again here.
        ///
        /// Result.Reason is null on success, or says why nothing was bought.
        /// </remarks>
        public async Task<SkillTreeCommitResult> CommitAsync(long playerId, byte[] target)
        {
            var result = new SkillTreeCommitResult { Reason = "Skill tree not loaded." };
            if (target.Length != WarTreeRegistry.Slots) return result;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();
                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeCommitResult { Reason = "No such player." };
                        return;
                    }

                    var rows = await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync();

                    var current = new byte[WarTreeRegistry.Slots];
                    foreach (var row in rows)
                        if (WarTreeRegistry.Get(row.BranchId) is { } n) current[row.BranchId] = (byte)Math.Clamp(row.Level, 0, n.MaxLevel);

                    // Modul: A WAR COMMIT NEVER TOUCHES A HEARTH SLOT (2026-10-30).
                    // The target is built from LoadRanksAsync, which now carries the
                    // Hearth ranks too, so they are taken out of the comparison here
                    // rather than refused as "no such skill".
                    target = (byte[])target.Clone();
                    foreach (var h in HearthTreeRegistry.All()) target[h.Id] = 0;

                    string? reason = WarTreeRegistry.PlanReason(current, target, player.AvailableSkillPoints, out int cost);
                    if (reason == null)
                    {
                        // A slot no War node sits in must stay empty.
                        for (int id = 0; id < target.Length; id++)
                            if (target[id] != 0 && WarTreeRegistry.Get(id) == null) { reason = "No such skill."; break; }
                    }
                    if (reason != null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeCommitResult { Reason = reason, Levels = Array.ConvertAll(current, b => (int)b), AvailableSkillPoints = player.AvailableSkillPoints };
                        return;
                    }

                    for (int id = 0; id < target.Length; id++)
                    {
                        if (target[id] == current[id]) continue;
                        var row = rows.FirstOrDefault(r => r.BranchId == id);
                        if (row == null) db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = id, Level = target[id] });
                        else row.Level = target[id];
                    }
                    player.AvailableSkillPoints -= cost;

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    for (int id = 0; id < target.Length; id++)
                    {
                        if (target[id] == current[id]) continue;
                        _playerRegistry?.SkillTreeSyncQueue.Enqueue(new SkillTreeSyncNotification
                        {
                            PlayerId = playerId,
                            BranchId = id,
                            NewLevel = target[id],
                            RemainingSkillPoints = player.AvailableSkillPoints,
                            FreeRespecUsed = (byte)(player.FreeRespecUsed ? 1 : 0),
                            PaidRespecGrants = (byte)Math.Clamp(player.PaidRespecGrants, 0, 255),
                        });
                    }

                    result = new SkillTreeCommitResult { Levels = Array.ConvertAll(target, b => (int)b), AvailableSkillPoints = player.AvailableSkillPoints, Spent = cost };
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });

            return result;
        }

        /// <summary>Craft points left: earned from the row's gathering mastery, minus the Hearth ranks held.</summary>
        public static int HearthPointsAvailable(PlayerRecord player, ReadOnlySpan<byte> ranks)
            => HearthPoints.Earned(player.WoodcuttingMasteryLevel, player.MiningMasteryLevel,
                    player.FishingMasteryLevel, player.HerbalismMasteryLevel)
               - HearthTreeRegistry.PointsSpent(ranks);

        /// <summary>
        /// Buys a whole drafted Hearth half at once: every rank in it, or none.
        /// </summary>
        /// <remarks>
        /// Modul: PAID WITH CRAFT POINTS, WHICH ARE NOT STORED (step 4.4). The
        /// balance is HearthPoints.Earned of the row's mastery minus the Hearth
        /// ranks under lock, so there is no counter to debit and none to drift.
        /// The mastery is the checkpointed row's, a little behind the live
        /// payload at most, which can only under-count. War slots in the target
        /// are ignored, as Hearth slots are in the War half's CommitAsync.
        /// </remarks>
        public async Task<SkillTreeCommitResult> CommitHearthAsync(long playerId, byte[] target)
        {
            var result = new SkillTreeCommitResult { Reason = "Skill tree not loaded." };
            if (target.Length != WarTreeRegistry.Slots) return result;
            target = (byte[])target.Clone();
            foreach (var w in WarTreeRegistry.All()) target[w.Id] = 0;

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();
                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeCommitResult { Reason = "No such player." };
                        return;
                    }

                    var rows = await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync();

                    var current = new byte[WarTreeRegistry.Slots];
                    foreach (var row in rows)
                        if (HearthTreeRegistry.Get(row.BranchId) is { } h) current[row.BranchId] = (byte)Math.Clamp(row.Level, 0, h.MaxLevel);

                    int available = HearthPointsAvailable(player, current);
                    string? reason = HearthTreeRegistry.PlanReason(current, target, available, out int cost);
                    if (reason == null)
                    {
                        for (int id = 0; id < target.Length; id++)
                            if (target[id] != 0 && HearthTreeRegistry.Get(id) == null) { reason = "No such skill."; break; }
                    }
                    if (reason != null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeCommitResult { Reason = reason, Levels = Array.ConvertAll(current, b => (int)b), AvailableSkillPoints = available };
                        return;
                    }

                    for (int id = 0; id < target.Length; id++)
                    {
                        if (target[id] == current[id]) continue;
                        var row = rows.FirstOrDefault(r => r.BranchId == id);
                        if (row == null) db.PlayerSkillTreeNodes.Add(new PlayerSkillTreeNode { PlayerId = playerId, BranchId = id, Level = target[id] });
                        else row.Level = target[id];
                    }

                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    // The War points ride every sync notification, unchanged:
                    // a Hearth purchase spends no skill point.
                    for (int id = 0; id < target.Length; id++)
                    {
                        if (target[id] == current[id]) continue;
                        _playerRegistry?.SkillTreeSyncQueue.Enqueue(new SkillTreeSyncNotification
                        {
                            PlayerId = playerId,
                            BranchId = id,
                            NewLevel = target[id],
                            RemainingSkillPoints = player.AvailableSkillPoints,
                            FreeRespecUsed = (byte)(player.FreeRespecUsed ? 1 : 0),
                            PaidRespecGrants = (byte)Math.Clamp(player.PaidRespecGrants, 0, 255),
                        });
                    }

                    result = new SkillTreeCommitResult { Levels = Array.ConvertAll(target, b => (int)b), AvailableSkillPoints = available - cost, Spent = cost };
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });

            return result;
        }

        /// <summary>
        /// Clears the Hearth half for gold: WarTreeRegistry.RespecGoldPrice of the
        /// craft points it held. Refunded is the craft points freed.
        /// </summary>
        /// <remarks>
        /// Modul: GOLD ONLY, AND NEVER THE WAR HALF'S FREE RESPEC (step 4.4).
        /// The free respec and the paid grants are one counter for the War half;
        /// letting a Hearth reset spend it would make a player choose which tree
        /// to fix. The price is the War half's formula on the Hearth points, so a
        /// reset costs the same per point in both halves.
        /// </remarks>
        public async Task<SkillTreeRespecResult> RespecHearthAsync(long playerId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var result = new SkillTreeRespecResult { Reason = "No such player." };

            var strategy = db.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
                try
                {
                    var player = await db.PlayerRecords
                        .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                        .SingleOrDefaultAsync();
                    if (player == null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeRespecResult { Reason = "No such player." };
                        return;
                    }

                    var rows = (await db.PlayerSkillTreeNodes
                        .FromSqlRaw("SELECT * FROM \"player_skill_tree\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                        .ToListAsync())
                        .Where(r => HearthTreeRegistry.Get(r.BranchId) != null)
                        .ToList();
                    var ranks = new byte[WarTreeRegistry.Slots];
                    foreach (var row in rows) ranks[row.BranchId] = (byte)Math.Clamp(row.Level, 0, HearthTreeRegistry.Get(row.BranchId)!.MaxLevel);
                    int spent = HearthTreeRegistry.PointsSpent(ranks);

                    var gold = await db.CommodityRecords.FirstOrDefaultAsync(c => c.PlayerId == playerId && c.ItemId == "gold");
                    long price = WarTreeRegistry.RespecGoldPrice(spent, player.CurrentLevel);
                    string? refusal = spent <= 0 ? "Nothing to refund."
                        : (gold?.Quantity ?? 0L) < price ? $"A Hearth respec costs {price:N0} gold."
                        : null;
                    if (refusal != null)
                    {
                        await transaction.RollbackAsync();
                        result = new SkillTreeRespecResult { Reason = refusal };
                        return;
                    }

                    db.PlayerSkillTreeNodes.RemoveRange(rows);
                    gold!.Quantity -= price;
                    await GoldLedger.RecordSpendAsync(db, playerId, GoldSpendCategory.Respec, price);
                    await db.SaveChangesAsync();
                    await transaction.CommitAsync();

                    result = new SkillTreeRespecResult { Refunded = spent, GoldSpent = price };

                    bool first = true;
                    foreach (var row in rows)
                    {
                        _playerRegistry?.SkillTreeSyncQueue.Enqueue(new SkillTreeSyncNotification
                        {
                            PlayerId = playerId,
                            BranchId = row.BranchId,
                            NewLevel = 0,
                            RemainingSkillPoints = player.AvailableSkillPoints,
                            FreeRespecUsed = (byte)(player.FreeRespecUsed ? 1 : 0),
                            PaidRespecGrants = (byte)Math.Clamp(player.PaidRespecGrants, 0, 255),
                            GoldSpent = first ? price : 0L,
                        });
                        first = false;
                    }
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }
            });

            return result;
        }

        /// <summary>
        /// Buys one rank of one War node: the old PurchaseSkillTreeLevel opcode,
        /// for a bundle that has not reloaded. A commit of "current plus one".
        /// An old bundle sends today's ids (0-19), which no longer name a node,
        /// so it is refused rather than spending on a tree that is gone.
        /// </summary>
        public async Task PurchaseLevelAsync(long playerId, int branchId)
        {
            if (WarTreeRegistry.Get(branchId) == null) return;
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            var target = await LoadRanksAsync(db, playerId);
            target[branchId]++;
            await CommitAsync(playerId, target);
        }
    }

    /// <summary>What a respec did: Reason is null when it refunded.</summary>
    public sealed class SkillTreeRespecResult
    {
        public string? Reason { get; set; }
        public int Refunded { get; set; }
        public long GoldSpent { get; set; }
    }

    /// <summary>What a drafted commit did: Reason is null when it bought everything.</summary>
    public sealed class SkillTreeCommitResult
    {
        public string? Reason { get; set; }
        // int[], not byte[]: System.Text.Json writes a byte[] as base64.
        public int[] Levels { get; set; } = Array.Empty<int>();
        public int AvailableSkillPoints { get; set; }
        public int Spent { get; set; }
    }

    /// <summary>
    /// A bought level on its way back to the tick thread, which owns the
    /// payload the level has to land on.
    /// </summary>
    public struct SkillTreeSyncNotification
    {
        public long PlayerId;
        public int BranchId;
        public byte NewLevel;
        public int RemainingSkillPoints;

        // Modul: carried because a RESPEC moves them, and the payload had no
        // other way to hear about it until the next full hydration - so the
        // button went on offering a free respec that was already spent.
        // A purchase leaves them unchanged and simply repeats the current
        // values, which is cheaper than a second queue.
        public byte FreeRespecUsed;
        public byte PaidRespecGrants;

        /// <summary>Gold a respec debited from the row, for the live balance to follow; 0 otherwise.</summary>
        public long GoldSpent;
    }
}
