using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Engine
{
    /// <summary>Where a recorded piece of equipment came from. Stored as a short.</summary>
    public enum DropSource : short
    {
        LiveKill = 1,
        BossGuarantee = 2,
        Offline = 3,
        Forge = 4,
        Craft = 5,
        FirstClearTrophy = 6,
        WorldBoss = 7,
        ChroniclePass = 8,
        OutboxRetry = 9,
        // A regional boss's guaranteed piece, won during offline catch-up.
        // Before it existed these were BossGuarantee, so "online or away?"
        // could not be asked of boss drops at all.
        OfflineBossGuarantee = 10,
    }

    /// <summary>
    /// What one transaction created, accumulated in memory and written once by
    /// <see cref="DropRecord.WriteAsync"/>. Not thread-safe: one per request.
    /// </summary>
    public sealed class DropTally
    {
        internal readonly Dictionary<(short Source, short Region, short Tier), (int Count, int Salvaged)> Counts = new();
        internal readonly List<(NotableItemEvent Event, EquipmentInstance? Instance)> Notables = new();

        public bool IsEmpty => Counts.Count == 0 && Notables.Count == 0;

        /// <summary>One piece that landed at <paramref name="finalTier"/>.</summary>
        public void Count(DropSource source, int regionTier, int finalTier, bool salvaged = false)
        {
            var key = ((short)source, (short)Math.Clamp(regionTier, 0, short.MaxValue), (short)Math.Clamp(finalTier, 0, short.MaxValue));
            Counts.TryGetValue(key, out var current);
            Counts[key] = (current.Count + 1, current.Salvaged + (salvaged ? 1 : 0));
        }

        /// <summary>A row for a rare piece. <paramref name="instance"/> may still be unsaved; its id is read at write time.</summary>
        public void Notable(long playerId, DropSource source, EquipmentInstance? instance, string baseItemId,
            int rolledTier, int finalTier, float lootLuckPct, DateTime nowUtc)
        {
            Notables.Add((new NotableItemEvent
            {
                PlayerId = playerId,
                BaseItemId = baseItemId ?? string.Empty,
                Source = (short)source,
                RolledTier = (short)rolledTier,
                FinalTier = (short)finalTier,
                LootLuckPct = lootLuckPct,
                CreatedAtUtc = nowUtc,
            }, instance));
        }

        public void Clear()
        {
            Counts.Clear();
            Notables.Clear();
        }
    }

    /// <summary>
    /// THE ONE WRITER of the drop record (loot_tier_daily_counts and
    /// notable_item_events). Task 26.
    /// </summary>
    /// <remarks>
    /// Modul: WHY IT EXISTS. "No Ancient+ in five days" had to be answered by
    /// reconstructing drop origins from row ids and affix-key shapes, because
    /// an EquipmentInstances row records neither when nor where it came from,
    /// and the chest a player sees is drops minus the sweep minus auto-salvage
    /// plus the forge. Counts for every piece created, a row for every rare
    /// one, both written INSIDE the transaction that creates the piece, so a
    /// rolled-back drop leaves no count behind (the retry outbox then records
    /// it under its original source when it replays).
    ///
    /// COST. One upsert statement per call, whatever the number of pieces - an
    /// offline catch-up passes thousands of kills in one request and this must
    /// stay O(tiers), not O(kills). Notable rows are ~1% of drops.
    ///
    /// EVERY SITE THAT CREATES AN EquipmentInstances ROW, and what it does here
    /// (`rg "EquipmentInstances.Add|new EquipmentInstance" server/FolkIdle.Server`):
    ///   CombatLootEngine.TryRollEquipment     LiveKill / Offline / BossGuarantee /
    ///                                         OfflineBossGuarantee
    ///   PendingGrantOutbox.TryApplyOneAsync   the original source (OutboxRetry
    ///                                         only for rows older than it)
    ///   ForgeSplicingEngine (fusion)          Forge - upgrades in place, no Add;
    ///                                         a notable row, no count
    ///   CraftingEngine                        Craft
    ///   CodexEngine (first-clear trophy)      FirstClearTrophy
    ///   SimulationEngine (chronicle claim)    ChroniclePass
    ///   WorldBossEngine (reward mail)         WorldBoss - counted when the mail is
    ///                                         written; the claim below is a move
    /// Deliberately NOT recorded - they move or restore items, they do not
    /// create new ones:
    ///   MarketEscrowEngine                    a sale/cancel returning a listed piece
    ///   MailboxAndBankEngine                  claiming mail (the item was counted
    ///                                         where the mail was written)
    ///   StarterEquipmentGrant                 the fixed kit every account starts with
    ///   DevFixtureSeeder                      dev only
    /// </remarks>
    public static class DropRecord
    {
        /// <summary>Legendary and above get a row of their own.</summary>
        public const int NotableTier = RarityTier.Legendary;

        public static bool IsNotable(int finalTier) => finalTier >= NotableTier;

        /// <summary>
        /// How long both tables keep rows. The record answers "what dropped
        /// lately and where from"; a count per player per day grows for ever
        /// otherwise, and nothing ever read a row older than a season.
        /// </summary>
        public const int RetentionDays = 180;

        /// <summary>Deletes counts and notable rows older than <see cref="RetentionDays"/>.</summary>
        public static async Task<int> PruneAsync(FolkIdleDbContext db, DateTime nowUtc, CancellationToken token = default)
        {
            DateTime cutoff = nowUtc.AddDays(-RetentionDays);
            DateOnly cutoffDay = DayOf(cutoff);
            int counts = await db.LootTierDailyCounts.Where(c => c.Day < cutoffDay).ExecuteDeleteAsync(token);
            int notables = await db.NotableItemEvents.Where(e => e.CreatedAtUtc < cutoff).ExecuteDeleteAsync(token);
            return counts + notables;
        }

        public static DateOnly DayOf(DateTime utc) => DateOnly.FromDateTime(utc);

        /// <summary>
        /// Writes a tally inside the caller's open transaction and clears it.
        /// Safe to call before or after the caller's own SaveChanges: if a
        /// notable piece is not saved yet, this saves first so the row can
        /// carry its id.
        /// </summary>
        public static async Task WriteAsync(FolkIdleDbContext db, long playerId, DropTally tally, DateTime nowUtc)
        {
            if (tally.IsEmpty) return;

            if (tally.Counts.Count > 0)
            {
                var sql = new StringBuilder(
                    "INSERT INTO loot_tier_daily_counts " +
                    "(\"PlayerId\", \"Day\", \"Source\", \"RegionTier\", \"QualityTier\", \"Count\", \"SalvagedCount\") VALUES ");
                var parameters = new List<object>(tally.Counts.Count * 7);
                DateOnly day = DayOf(nowUtc);
                int row = 0;
                foreach (var (key, value) in tally.Counts)
                {
                    if (row++ > 0) sql.Append(", ");
                    int p = parameters.Count;
                    sql.Append($"({{{p}}}, {{{p + 1}}}, {{{p + 2}}}, {{{p + 3}}}, {{{p + 4}}}, {{{p + 5}}}, {{{p + 6}}})");
                    parameters.Add(playerId);
                    parameters.Add(day);
                    parameters.Add(key.Source);
                    parameters.Add(key.Region);
                    parameters.Add(key.Tier);
                    parameters.Add(value.Count);
                    parameters.Add(value.Salvaged);
                }
                sql.Append(
                    " ON CONFLICT (\"PlayerId\", \"Day\", \"Source\", \"RegionTier\", \"QualityTier\") DO UPDATE SET " +
                    "\"Count\" = loot_tier_daily_counts.\"Count\" + EXCLUDED.\"Count\", " +
                    "\"SalvagedCount\" = loot_tier_daily_counts.\"SalvagedCount\" + EXCLUDED.\"SalvagedCount\"");

                await db.Database.ExecuteSqlRawAsync(sql.ToString(), parameters);
            }

            if (tally.Notables.Count > 0)
            {
                bool unsaved = false;
                foreach (var (_, instance) in tally.Notables)
                {
                    if (instance != null && instance.Id == 0) unsaved = true;
                }
                if (unsaved) await db.SaveChangesAsync();

                foreach (var (notable, instance) in tally.Notables)
                {
                    notable.EquipmentInstanceId = instance != null && instance.Id > 0 ? instance.Id : null;
                    db.NotableItemEvents.Add(notable);
                }
                await db.SaveChangesAsync();
            }

            tally.Clear();
        }

        /// <summary>
        /// One piece, for the single-grant writers (forge, craft, trophy,
        /// chronicle): a count, plus a row when it is notable or
        /// <paramref name="alwaysNotable"/>.
        /// </summary>
        /// <remarks>
        /// <paramref name="countPiece"/> is false for a writer that changes an
        /// existing piece rather than creating one (the forge's fusion): that
        /// piece was already counted at its old tier when it dropped, so
        /// counting it again at the new tier made one item two in the per-tier
        /// totals. Its notable row still says what it became.
        /// </remarks>
        public static Task RecordOneAsync(
            FolkIdleDbContext db, long playerId, DropSource source, int regionTier,
            EquipmentInstance? instance, string baseItemId, int rolledTier, int finalTier,
            bool alwaysNotable = false, bool countPiece = true)
        {
            var tally = new DropTally();
            DateTime now = DateTime.UtcNow;
            if (countPiece) tally.Count(source, regionTier, finalTier);
            if (alwaysNotable || IsNotable(finalTier))
            {
                tally.Notable(playerId, source, instance, baseItemId, rolledTier, finalTier, 0f, now);
            }
            return WriteAsync(db, playerId, tally, now);
        }
    }
}
