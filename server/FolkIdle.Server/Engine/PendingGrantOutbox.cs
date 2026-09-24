using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The shared read/write surface for pending_grants. Enqueue is called
    /// from inside an existing catch block, after the normal write already
    /// failed and its transaction already rolled back. Apply is called by
    /// the drain worker (and directly by this task's own tests, before that
    /// worker exists) to replay a row's already-resolved outcome.
    ///
    /// Modul: THE OUTBOX STORES THE RESOLVED GRANT, NEVER THE RECIPE.
    /// Combat loot rolls a random equipment tier, a random item and random
    /// affixes BEFORE the database write that can fail; gathering and
    /// offline production compute deterministic commodity deltas before
    /// their write. By the time a catch block runs, the outcome is already
    /// decided - only the write failed. A retry must apply that EXACT
    /// outcome; re-running the original method would re-roll the dice.
    /// </summary>
    public static class PendingGrantOutbox
    {
        // Modul: one counter per SourceType, not one global counter - see
        // the plan's Global Constraints for why this is in-memory and not a
        // DB-derived value recomputed from the payload. Seeded from the
        // table's own high-water mark at first use so a process restart
        // never reissues a number a still-pending row already holds.
        private static readonly long[] _nextSequence = new long[4]; // index 0 unused, 1..3 = PendingGrantSourceType values
        private static readonly bool[] _seeded = new bool[4];
        private static readonly object _seedLock = new();

        private static async Task EnsureSeededAsync(FolkIdleDbContext db, int sourceType)
        {
            if (Volatile.Read(ref _seeded[sourceType])) return;
            lock (_seedLock)
            {
                if (_seeded[sourceType]) return;
            }

            long max = await db.PendingGrants
                .Where(g => g.SourceType == sourceType)
                .Select(g => (long?)g.SourceSequence)
                .MaxAsync() ?? 0L;

            lock (_seedLock)
            {
                if (!_seeded[sourceType])
                {
                    _nextSequence[sourceType] = max;
                    _seeded[sourceType] = true;
                }
            }
        }

        private static long NextSourceSequence(int sourceType) => Interlocked.Increment(ref _nextSequence[sourceType]);

        /// <summary>
        /// Persists an already-resolved set of commodity deltas (materials
        /// and/or gold, by ItemId) for retry. Called from a catch block AFTER
        /// the normal transaction has rolled back - this opens its OWN short
        /// transaction on the same DbContext, since the caller's is already
        /// gone.
        /// </summary>
        public static async Task EnqueueCommodityDeltasAsync(
            FolkIdleDbContext db, long playerId, int sourceType, IReadOnlyDictionary<string, long> deltas)
        {
            if (deltas.Count == 0) return;
            await EnsureSeededAsync(db, sourceType);

            long sequence = NextSourceSequence(sourceType);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payloadJson = JsonSerializer.Serialize(deltas);

            // ON CONFLICT DO NOTHING: the unique (PlayerId, SourceType,
            // SourceSequence) index makes a duplicate enqueue for the same
            // logical failure a no-op rather than a second row. See the
            // plan's Global Constraints on what this does and does not
            // protect against.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO pending_grants
                    (""PlayerId"", ""SourceType"", ""SourceSequence"", ""PayloadKind"", ""PayloadJson"",
                     ""CreatedAtEpochMs"", ""NextAttemptAtEpochMs"", ""AttemptCount"")
                VALUES
                    ({playerId}, {sourceType}, {sequence}, {PendingGrantPayloadKind.CommodityDeltas}, {payloadJson},
                     {nowMs}, {nowMs}, 0)
                ON CONFLICT (""PlayerId"", ""SourceType"", ""SourceSequence"") DO NOTHING");
        }

        /// <summary>
        /// Persists an already-resolved equipment drop for retry. Same shape
        /// as EnqueueCommodityDeltasAsync - see PendingGrantPayloadKind.EquipmentGrant.
        /// </summary>
        public static Task EnqueueEquipmentGrantAsync(
            FolkIdleDbContext db, long playerId, int sourceType, string baseItemId, int qualityTier, string affixPayload)
            => EnqueueEquipmentGrantAsync(db, playerId, sourceType,
                new EquipmentGrantPayload { BaseItemId = baseItemId, QualityTier = qualityTier, AffixPayload = affixPayload });

        /// <summary>The same, carrying the drop record's origin fields (task 26).</summary>
        public static async Task EnqueueEquipmentGrantAsync(
            FolkIdleDbContext db, long playerId, int sourceType, EquipmentGrantPayload payload)
        {
            await EnsureSeededAsync(db, sourceType);

            long sequence = NextSourceSequence(sourceType);
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string payloadJson = JsonSerializer.Serialize(payload);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO pending_grants
                    (""PlayerId"", ""SourceType"", ""SourceSequence"", ""PayloadKind"", ""PayloadJson"",
                     ""CreatedAtEpochMs"", ""NextAttemptAtEpochMs"", ""AttemptCount"")
                VALUES
                    ({playerId}, {sourceType}, {sequence}, {PendingGrantPayloadKind.EquipmentGrant}, {payloadJson},
                     {nowMs}, {nowMs}, 0)
                ON CONFLICT (""PlayerId"", ""SourceType"", ""SourceSequence"") DO NOTHING");
        }

        /// <summary>
        /// Applies one row's resolved payload inside the CALLER's transaction
        /// and stages the row for deletion (via the caller's SaveChanges) -
        /// this method does not commit anything itself, so success and the
        /// row's removal are always one atomic unit. Returns false only for
        /// an unrecognized PayloadKind (a version-skew bug, not a transient
        /// failure) so the caller can decide how to record that separately
        /// from an ordinary retryable exception.
        /// </summary>
        public static async Task<bool> TryApplyOneAsync(FolkIdleDbContext db, PendingGrant row)
        {
            switch (row.PayloadKind)
            {
                case PendingGrantPayloadKind.CommodityDeltas:
                    var deltas = JsonSerializer.Deserialize<Dictionary<string, long>>(row.PayloadJson)!;
                    foreach (var (itemId, amount) in deltas)
                    {
                        if (amount == 0) continue;
                        var commodity = await db.CommodityRecords
                            .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {row.PlayerId} AND \"ItemId\" = {itemId} FOR UPDATE")
                            .SingleOrDefaultAsync();
                        if (commodity == null)
                        {
                            db.CommodityRecords.Add(new CommodityRecord { PlayerId = row.PlayerId, ItemId = itemId, Quantity = Math.Max(0L, amount) });
                        }
                        else
                        {
                            commodity.Quantity = Math.Max(0L, commodity.Quantity + amount);
                        }
                    }
                    return true;

                case PendingGrantPayloadKind.EquipmentGrant:
                    var grant = JsonSerializer.Deserialize<EquipmentGrantPayload>(row.PayloadJson)!;
                    var instance = new EquipmentInstance
                    {
                        BaseItemId = grant.BaseItemId,
                        PlayerId = row.PlayerId,
                        QualityTier = grant.QualityTier,
                        AffixPayload = grant.AffixPayload,
                        IsAffixLocked = false
                    };
                    db.EquipmentInstances.Add(instance);

                    // Modul: THE DROP RECORD (task 26). The failed transaction
                    // rolled its counts back with it, so without this the record
                    // under-counts exactly the drops the outbox saved. Recorded
                    // as OutboxRetry in the caller's transaction, so it commits
                    // with the piece. The original source is not lost: it is in
                    // this row's payload, and the notable row keeps the roll's
                    // own tier and luck.
                    var tally = new DropTally();
                    DateTime now = DateTime.UtcNow;
                    tally.Count(DropSource.OutboxRetry, grant.RegionTier, grant.QualityTier);
                    if (DropRecord.IsNotable(grant.QualityTier))
                    {
                        tally.Notable(row.PlayerId, DropSource.OutboxRetry, instance, grant.BaseItemId,
                            grant.RolledTier > 0 ? grant.RolledTier : grant.QualityTier, grant.QualityTier, grant.LootLuckPct, now);
                    }
                    await DropRecord.WriteAsync(db, row.PlayerId, tally, now);
                    return true;

                default:
                    return false;
            }
        }
    }

    /// <summary>PayloadJson shape for PendingGrantPayloadKind.EquipmentGrant. See CombatLootEngine.TryRollEquipment for where these three values are already decided before the write that can fail.</summary>
    public struct EquipmentGrantPayload
    {
        public string BaseItemId { get; set; }
        public int QualityTier { get; set; }
        public string AffixPayload { get; set; }

        // Modul: the drop record's origin fields (task 26). Absent - so 0 - on
        // rows written before they existed; the replay treats 0 as "unknown"
        // and still records the piece.
        public int RegionTier { get; set; }
        public int RolledTier { get; set; }
        public float LootLuckPct { get; set; }
        /// <summary>The DropSource the failed write would have recorded.</summary>
        public short OriginalSource { get; set; }
    }
}
