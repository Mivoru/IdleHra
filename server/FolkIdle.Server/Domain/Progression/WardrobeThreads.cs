using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>A thread-only frame in the Wardrobe shop: never in a chest, never on the market.</summary>
    public sealed record ThreadExclusive(string Id, string Name, int Price);

    /// <summary>
    /// Wardrobe threads (owner, 2026-11-15): what a duplicate is worth.
    /// </summary>
    /// <remarks>
    /// Modul: WHY. Measured on production the week to 2026-11-15: the three
    /// players owned 25-31 of the chest avatars and 14-31 of the frames - the
    /// collection was complete, so nearly every chest was a duplicate, and 223
    /// and 328 chests sat unopened. A duplicate now unravels into threads, and
    /// threads buy a chosen piece or a thread-only frame, so every chest moves
    /// the player toward something.
    ///
    /// PRICES ARE MEASURED. Unravelling the spare copies already owned gives
    /// ~1,440 / ~984 / ~189 threads (plus ~740 / 0 / ~600 from the unopened
    /// chests), so the four exclusives at 250 / 400 / 600 / 1,000 let the
    /// biggest collector buy three at once and the others two, and then about
    /// one more every month or two at the quartered drop rate.
    /// </remarks>
    public static class WardrobeThreads
    {
        /// <summary>Threads for one duplicate, by its rarity (index 1-4).</summary>
        public static readonly int[] PerDuplicate = { 0, 1, 3, 10, 30 };

        /// <summary>The price of a chosen chest piece, by its rarity - ten duplicates of the same rarity.</summary>
        public static readonly int[] ShopPrice = { 0, 10, 30, 100, 300 };

        /// <summary>
        /// The thread-only frames. Mokosh is the Slavic spinner and weaver
        /// goddess, so the threads' own prizes are hers.
        /// </summary>
        public static readonly IReadOnlyList<ThreadExclusive> Exclusives = new[]
        {
            new ThreadExclusive("frame_thread_spindle", "Spindle Wool", 250),
            new ThreadExclusive("frame_thread_loom", "Loom Tapestry", 400),
            new ThreadExclusive("frame_thread_golden", "Golden Thread", 600),
            new ThreadExclusive("frame_thread_mokosh", "Mokosh's Crown", 1000),
        };

        public static IEnumerable<CosmeticDefinition> Cosmetics()
            => Exclusives.Select(e => new CosmeticDefinition(e.Id, CosmeticKind.Frame, CosmeticRegistry.Legendary, e.Name, null, Bound: true));

        /// <summary>What <paramref name="definitionId"/> costs in threads, or 0 when threads cannot buy it.</summary>
        public static int PriceOf(string definitionId)
        {
            var exclusive = Exclusives.FirstOrDefault(e => e.Id == definitionId);
            if (exclusive != null) return exclusive.Price;
            var def = CosmeticRegistry.Find(definitionId);
            if (def == null || def.Bound || def.Kind == CosmeticKind.Chest) return 0;
            return ShopPrice[Math.Clamp(def.Rarity, 0, CosmeticRegistry.MaxRarity)];
        }

        public static async Task<long> BalanceAsync(FolkIdleDbContext db, long playerId)
            => await db.PlayerWardrobes.AsNoTracking().Where(w => w.PlayerId == playerId).Select(w => w.Threads).FirstOrDefaultAsync();

        /// <summary>Adds threads inside the caller's transaction.</summary>
        internal static Task AddAsync(FolkIdleDbContext db, long playerId, long threads)
            => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"PlayerWardrobes\" (\"PlayerId\", \"Threads\") VALUES ({0}, {1}) " +
                "ON CONFLICT (\"PlayerId\") DO UPDATE SET \"Threads\" = \"PlayerWardrobes\".\"Threads\" + EXCLUDED.\"Threads\"",
                playerId, threads);

        public enum UnravelOutcome { Ok, NothingSpare }

        /// <summary>
        /// Unravels every spare copy: of each chest piece the player has more
        /// than one un-listed copy of, all but one become threads. Bound pieces
        /// and listed copies are never touched, and one copy always stays, so
        /// whatever is worn stays owned.
        /// </summary>
        public static async Task<(UnravelOutcome Outcome, int Pieces, long Threads)> UnravelAsync(FolkIdleDbContext db, long playerId)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                await db.Database.ExecuteSqlRawAsync("SELECT 1 FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId);
                var rows = await db.CosmeticItems
                    .Where(c => c.PlayerId == playerId && c.Kind != (byte)CosmeticKind.Chest && !c.IsListed)
                    .OrderBy(c => c.Id)
                    .ToListAsync();

                var spare = rows
                    .Where(r => CosmeticRegistry.Find(r.DefinitionId) is { Bound: false })
                    .GroupBy(r => r.DefinitionId)
                    .SelectMany(g => g.Skip(1))
                    .ToList();
                if (spare.Count == 0)
                {
                    await tx.SafeRollbackAsync();
                    return (UnravelOutcome.NothingSpare, 0, 0);
                }

                long threads = spare.Sum(r => (long)PerDuplicate[Math.Clamp((int)r.Rarity, 0, CosmeticRegistry.MaxRarity)]);
                db.CosmeticItems.RemoveRange(spare);
                await db.SaveChangesAsync();
                await AddAsync(db, playerId, threads);
                await tx.CommitAsync();
                return (UnravelOutcome.Ok, spare.Count, threads);
            }
            catch
            {
                await tx.SafeRollbackAsync();
                throw;
            }
        }

        public enum BuyOutcome { Ok, NotForSale, AlreadyOwned, NotEnoughThreads }

        /// <summary>Buys one piece for threads. A piece already owned is refused: threads are for what is missing.</summary>
        public static async Task<BuyOutcome> BuyAsync(FolkIdleDbContext db, long playerId, string definitionId, DateTime utcNow)
        {
            int price = PriceOf(definitionId);
            var def = CosmeticRegistry.Find(definitionId);
            if (price <= 0 || def == null) return BuyOutcome.NotForSale;

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            try
            {
                var wallet = await db.PlayerWardrobes
                    .FromSqlRaw("SELECT * FROM \"PlayerWardrobes\" WHERE \"PlayerId\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();
                if (wallet == null || wallet.Threads < price)
                {
                    await tx.SafeRollbackAsync();
                    return BuyOutcome.NotEnoughThreads;
                }
                if (await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == definitionId))
                {
                    await tx.SafeRollbackAsync();
                    return BuyOutcome.AlreadyOwned;
                }

                wallet.Threads -= price;
                db.CosmeticItems.Add(new CosmeticItem
                {
                    PlayerId = playerId,
                    Kind = (byte)def.Kind,
                    DefinitionId = def.Id,
                    Rarity = (byte)def.Rarity,
                    Source = (byte)CosmeticSource.Threads,
                    AcquiredAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
                });
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return BuyOutcome.Ok;
            }
            catch
            {
                await tx.SafeRollbackAsync();
                throw;
            }
        }
    }
}
