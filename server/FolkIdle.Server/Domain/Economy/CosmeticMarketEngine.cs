using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Domain.Economy
{
    public enum CosmeticMarketResult
    {
        Ok = 0,
        /// <summary>No such item or listing - or it was sold a moment ago.</summary>
        NotFound,
        /// <summary>The item or listing belongs to someone else.</summary>
        NotYours,
        /// <summary>The seller is wearing their only copy.</summary>
        Worn,
        AlreadyListed,
        /// <summary>Outside 1 - 1,000,000,000 gold.</summary>
        InvalidPrice,
        /// <summary>The market needs a guild, as it does for equipment.</summary>
        NoGuildLicense,
        InsufficientGold,
        /// <summary>A seller cannot buy their own listing - cancel it instead.</summary>
        OwnListing,
        /// <summary>An anti-cheat quarantined account cannot trade.</summary>
        Quarantined,
        PlayerNotFound,
    }

    public sealed record CosmeticListingView(
        long Id, long SellerId, string SellerName, long CosmeticItemId, string DefinitionId,
        byte Kind, byte Rarity, long Price, bool IsMine);

    /// <summary>What a completed sale did, so the caller can tell the live sessions.</summary>
    public sealed record CosmeticSale(long SellerId, long SellerProceeds);

    /// <summary>
    /// Task 54 phase 4: selling cosmetics and unopened chests between players.
    ///
    /// The same market rules as equipment - a guild trade license on both
    /// sides, the wealth-bracket fee burned and the seller's guild tax, both
    /// through MarketEscrowEngine's shared helpers so the two markets cannot
    /// drift - with one deliberate difference: THE SELLER SETS THE PRICE
    /// (owner, 2026-09-28). No corridor; only the arithmetic's own fences.
    ///
    /// Modul: THE TWO GOLD PATHS. The buyer's gold is a DB DEBIT on the locked
    /// CommodityRecords row (never RedisPendingGoldDelta, which the checkpoint
    /// applies as an increment), and the caller enqueues ReloadState for the
    /// buyer. The seller is paid exactly as an equipment sale pays: through
    /// MarketMatchQueue when online (AddGold on the tick, which is the
    /// no-DB-row path), else straight onto the row.
    /// </summary>
    public static class CosmeticMarketEngine
    {
        public const int MaxListingsShown = 200;

        public static async Task<List<CosmeticListingView>> ListingsAsync(FolkIdleDbContext db, long viewerId, int? kind, int? rarity)
        {
            var query = db.CosmeticListings.AsNoTracking().AsQueryable();
            if (kind.HasValue) query = query.Where(l => l.Kind == kind.Value);
            if (rarity.HasValue) query = query.Where(l => l.Rarity == rarity.Value);

            // The viewer's own listings always come back, so they can be cancelled
            // even when the cheapest 200 are someone else's.
            var cheapest = await query.OrderBy(l => l.Price).ThenBy(l => l.Id).Take(MaxListingsShown).ToListAsync();
            var mine = await db.CosmeticListings.AsNoTracking().Where(l => l.SellerId == viewerId).ToListAsync();
            var rows = cheapest.Concat(mine.Where(m => cheapest.All(c => c.Id != m.Id))).ToList();

            var sellerIds = rows.Select(r => r.SellerId).Distinct().ToList();
            var names = await db.PlayerRecords.AsNoTracking()
                .Where(p => sellerIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Username })
                .ToDictionaryAsync(p => p.Id, p => p.Username);

            return rows
                .Where(r => CosmeticRegistry.Find(r.DefinitionId) != null)
                .Select(r => new CosmeticListingView(
                    r.Id, r.SellerId, names.TryGetValue(r.SellerId, out var n) && !string.IsNullOrEmpty(n) ? n : $"Player #{r.SellerId}",
                    r.CosmeticItemId, r.DefinitionId, r.Kind, r.Rarity, r.Price, r.SellerId == viewerId))
                .ToList();
        }

        public static async Task<CosmeticMarketResult> ListAsync(FolkIdleDbContext db, long sellerId, long cosmeticItemId, long price, DateTime utcNow)
        {
            if (price < CosmeticRegistry.MinMarketPrice || price > CosmeticRegistry.MaxMarketPrice)
            {
                return CosmeticMarketResult.InvalidPrice;
            }

            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var seller = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", sellerId)
                .SingleOrDefaultAsync();
            var gate = GateTrader(seller);
            if (gate != CosmeticMarketResult.Ok)
            {
                await tx.RollbackAsync();
                return gate;
            }

            var item = await db.CosmeticItems
                .FromSqlRaw("SELECT * FROM cosmetic_items WHERE \"Id\" = {0} FOR UPDATE", cosmeticItemId)
                .SingleOrDefaultAsync();
            if (item == null || CosmeticRegistry.Find(item.DefinitionId) == null)
            {
                await tx.RollbackAsync();
                return CosmeticMarketResult.NotFound;
            }
            if (item.PlayerId != sellerId)
            {
                await tx.RollbackAsync();
                return CosmeticMarketResult.NotYours;
            }
            if (item.IsListed)
            {
                await tx.RollbackAsync();
                return CosmeticMarketResult.AlreadyListed;
            }

            // Worn = the seller wears this definition and this is their last
            // un-listed copy. Selling a duplicate of what you wear is fine.
            bool wornDefinition = item.DefinitionId == seller!.EquippedAvatarId || item.DefinitionId == seller.EquippedFrameId;
            if (wornDefinition)
            {
                int otherCopies = await db.CosmeticItems.CountAsync(c =>
                    c.PlayerId == sellerId && c.DefinitionId == item.DefinitionId && !c.IsListed && c.Id != item.Id);
                if (otherCopies == 0)
                {
                    await tx.RollbackAsync();
                    return CosmeticMarketResult.Worn;
                }
            }

            item.IsListed = true;
            db.CosmeticListings.Add(new CosmeticListing
            {
                SellerId = sellerId,
                CosmeticItemId = item.Id,
                DefinitionId = item.DefinitionId,
                Kind = item.Kind,
                Rarity = item.Rarity,
                Price = price,
                CreatedAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
            });
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return CosmeticMarketResult.Ok;
        }

        public static async Task<CosmeticMarketResult> CancelAsync(FolkIdleDbContext db, long sellerId, long listingId)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var listing = await db.CosmeticListings
                .FromSqlRaw("SELECT * FROM cosmetic_market_listings WHERE \"Id\" = {0} FOR UPDATE", listingId)
                .SingleOrDefaultAsync();
            if (listing == null)
            {
                await tx.RollbackAsync();
                return CosmeticMarketResult.NotFound;
            }
            if (listing.SellerId != sellerId)
            {
                await tx.RollbackAsync();
                return CosmeticMarketResult.NotYours;
            }

            var item = await db.CosmeticItems
                .FromSqlRaw("SELECT * FROM cosmetic_items WHERE \"Id\" = {0} FOR UPDATE", listing.CosmeticItemId)
                .SingleOrDefaultAsync();
            if (item != null) item.IsListed = false;
            db.CosmeticListings.Remove(listing);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return CosmeticMarketResult.Ok;
        }

        /// <summary>
        /// Buys a listing: debits the buyer's gold row, hands the item over,
        /// burns the fee, pays the guild tax and credits the seller - one
        /// transaction. Answers the sale so the caller can tell both sessions.
        /// </summary>
        public static async Task<(CosmeticMarketResult Result, CosmeticSale? Sale)> BuyAsync(
            FolkIdleDbContext db, PlayerSessionRegistry? registry, long buyerId, long listingId, DateTime utcNow)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var listing = await db.CosmeticListings
                .FromSqlRaw("SELECT * FROM cosmetic_market_listings WHERE \"Id\" = {0} FOR UPDATE", listingId)
                .SingleOrDefaultAsync();
            if (listing == null)
            {
                await tx.RollbackAsync();
                return (CosmeticMarketResult.NotFound, null);
            }
            if (listing.SellerId == buyerId)
            {
                await tx.RollbackAsync();
                return (CosmeticMarketResult.OwnListing, null);
            }

            var buyer = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", buyerId)
                .SingleOrDefaultAsync();
            var gate = GateTrader(buyer);
            if (gate != CosmeticMarketResult.Ok)
            {
                await tx.RollbackAsync();
                return (gate, null);
            }

            var buyerGold = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", buyerId)
                .SingleOrDefaultAsync();
            if (buyerGold == null || buyerGold.Quantity < listing.Price)
            {
                await tx.RollbackAsync();
                return (CosmeticMarketResult.InsufficientGold, null);
            }

            var item = await db.CosmeticItems
                .FromSqlRaw("SELECT * FROM cosmetic_items WHERE \"Id\" = {0} FOR UPDATE", listing.CosmeticItemId)
                .SingleOrDefaultAsync();
            if (item == null || item.PlayerId != listing.SellerId)
            {
                // The item left the seller some other way; the listing is dead.
                db.CosmeticListings.Remove(listing);
                await db.SaveChangesAsync();
                await tx.CommitAsync();
                return (CosmeticMarketResult.NotFound, null);
            }

            long price = listing.Price;
            buyerGold.Quantity -= price;
            await GoldLedger.RecordSpendAsync(db, buyerId, GoldSpendCategory.Cosmetics, price);

            item.PlayerId = buyerId;
            item.IsListed = false;
            item.Source = (byte)CosmeticSource.Market;
            item.AcquiredAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);

            var sellerGold = await db.CommodityRecords
                .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", listing.SellerId)
                .SingleOrDefaultAsync();
            long fee = (long)(price * MarketEscrowEngine.WealthFeeRate(sellerGold?.Quantity ?? 0L));
            long guildTax = await MarketEscrowEngine.ApplyGuildSalesTaxAsync(db, listing.SellerId, price);
            long proceeds = Math.Max(0L, price - fee - guildTax);

            // Seller payment: the equipment market's exact choice. Online, the
            // tick pays it (the payload holds unbanked gold, so a row credit
            // here would double-pay at the next checkpoint); offline, the row.
            bool sellerOnline = registry?.IsPlayerOnline(listing.SellerId) ?? false;
            if (!sellerOnline && proceeds > 0L)
            {
                await CommodityLedger.AddAsync(db, listing.SellerId, "gold", proceeds);
            }

            db.HistoricalMarketArchives.Add(new HistoricalMarketArchive
            {
                OriginalOrderId = listing.Id,
                SellerId = listing.SellerId,
                BuyerId = buyerId,
                ExecutionPrice = price,
                FeeBurned = fee,
                OrderType = "COSMETIC",
                BaseItemId = listing.DefinitionId,
                QualityTier = listing.Rarity,
                ExecutionTimestampEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            });
            db.CosmeticListings.Remove(listing);
            await db.SaveChangesAsync();
            await tx.CommitAsync();

            // After the commit, never inside it: a queued payment for a sale
            // that then rolled back would be gold from nowhere.
            if (sellerOnline && proceeds > 0L)
            {
                registry!.MarketMatchQueue.Enqueue(new MarketMatchNotification
                {
                    PlayerId = listing.SellerId,
                    GoldDelta = proceeds,
                    NewEquipmentInstanceId = null,
                });
            }

            return (CosmeticMarketResult.Ok, new CosmeticSale(listing.SellerId, proceeds));
        }

        private static CosmeticMarketResult GateTrader(PlayerRecord? player)
        {
            if (player == null) return CosmeticMarketResult.PlayerNotFound;
            if (player.Quarantine_Active || player.IsQuarantined) return CosmeticMarketResult.Quarantined;
            if (player.GuildId <= 0) return CosmeticMarketResult.NoGuildLicense;
            return CosmeticMarketResult.Ok;
        }
    }
}
