using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using System.Data;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class MarketOrderBookEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public MarketOrderBookEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        // Modul 40/51: 7-day rolling average execution price for this base
        // item + quality tier, computed from real completed-order history
        // (HistoricalMarketArchives). When no recent completed trades exist
        // (a brand-new or rarely-traded listing), falls back to a
        // deterministic baseline (BaseValueGold * QualityTierMultiplier)
        // pulled from ContentRegistry, rather than disabling the corridor -
        // an untraded item must not be listable at an arbitrary price. Only
        // returns null if the item is not a recognized ContentRegistry entry
        // at all, in which case there is genuinely nothing to validate against.
        internal static async Task<double?> CalculateRollingAveragePriceAsync(FolkIdleDbContext db, string baseItemId, int qualityTier)
        {
            long windowStartEpoch = DateTimeOffset.UtcNow.AddDays(-7).ToUnixTimeMilliseconds();

            var recentPrices = await db.HistoricalMarketArchives
                .AsNoTracking()
                .Where(a => a.BaseItemId == baseItemId && a.QualityTier == qualityTier && a.ExecutionTimestampEpoch >= windowStartEpoch)
                .Select(a => (double)a.ExecutionPrice)
                .ToListAsync();

            if (recentPrices.Count > 0)
            {
                double sum = 0.0;
                for (int i = 0; i < recentPrices.Count; i++)
                {
                    sum += recentPrices[i];
                }

                return sum / recentPrices.Count;
            }

            if (ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out ItemDefinition definition))
            {
                double qualityTierMultiplier = 1.0 + (qualityTier * 0.5);
                return definition.BaseValueGold * qualityTierMultiplier;
            }

            return null;
        }

        // Modul 40: paginated read of currently active SELL listings for the
        // marketplace browser. Deterministic ordering (Price ascending, then
        // CreatedAtEpoch ascending as the tiebreak) keeps page N stable across
        // repeated requests even as unrelated listings are created/filled
        // between pages - callers must clamp pageIndex/pageSize themselves
        // (see ClientCommandValidator.ValidateMarketBrowserQuery) before this
        // runs an unbounded Skip/Take against the caller-supplied values.
        // isQuarantined must be the requesting player's own flag - matching
        // MarketEscrowEngine.BuyItemAsync's isolation check, a browser must
        // never surface listings the requester could not actually buy (or let
        // a quarantined player see the real, non-isolated economy).
        public static async Task<System.Collections.Generic.List<MarketOrderRecord>> FetchActiveListingsAsync(FolkIdleDbContext db, string baseItemId, int qualityTier, bool isQuarantined, int pageIndex, int pageSize)
        {
            var page = await BrowseActiveListingsAsync(db, new MarketBrowseQuery
            {
                BaseItemId = baseItemId,
                MinQualityTier = qualityTier,
                MaxQualityTier = qualityTier,
                IsQuarantined = isQuarantined,
                PageIndex = pageIndex,
                PageSize = pageSize,
            });
            return page.Listings;
        }

        /// <summary>
        /// What the browser asks for.
        ///
        /// Modul: THE MARKET WAS NOT BROWSABLE. Its only query required an
        /// exact BaseItemId and an exact QualityTier and 400'd without them, so
        /// a player could look up
        /// "eq_steel_claymore_melee_weapon_slot_base at tier 7" and could not,
        /// under any circumstances, see what was for sale. On a marketplace
        /// meant to hold every player's spare gear that is not a search, it is
        /// a lock.
        /// </summary>
        public sealed class MarketBrowseQuery
        {
            /// <summary>Substring match, not equality. Empty means everything.</summary>
            public string BaseItemId = string.Empty;
            /// <summary>
            /// EquipmentSlotEngine slot indices to include. Empty means every
            /// slot.
            ///
            /// Modul: a SET, not a single index. "Show me helmets" is a rarer
            /// question than "show me helmets, chests and leggings" - a player
            /// shopping for armour wants several types at once, and a
            /// single-value filter made them page through the book once per
            /// type.
            /// </summary>
            public System.Collections.Generic.HashSet<int> SlotIndices = new();

            /// <summary>
            /// Region tiers (1-5) to include. Empty means every tier.
            ///
            /// Resolved from the item's own RegionTier, which is the LOCATION
            /// its gear belongs to - not QualityTier, which is the 14-step
            /// rarity of the individual roll. Two different axes that both get
            /// called "tier" in conversation, and a player asking for "tier 3
            /// gear" means the Scorched Wasteland set, not a Rare.
            /// </summary>
            public System.Collections.Generic.HashSet<int> RegionTiers = new();
            public int MinQualityTier;
            public int MaxQualityTier = 13;
            public bool IsQuarantined;
            public int PageIndex;
            public int PageSize = 24;
            /// <summary>price | rarity | name, ascending unless Descending.</summary>
            public string SortBy = "price";
            public bool Descending;
        }

        public sealed class MarketBrowsePage
        {
            public System.Collections.Generic.List<MarketOrderRecord> Listings = new();
            public int TotalCount;
        }

        /// <summary>
        /// Every open order the player owns - listings and resting limit
        /// orders, both sides - newest first.
        ///
        /// Modul: task 102's "My orders". A player could list a piece, place a
        /// buy order that escrowed their gold, and then had no screen that said
        /// either existed: the book is browsed by item, never by owner. Bounded
        /// so a pathological account cannot make this a book dump.
        /// </summary>
        public const int MaxOwnOrders = 200;

        public static Task<System.Collections.Generic.List<MarketOrderRecord>> FetchOwnOpenOrdersAsync(FolkIdleDbContext db, long playerId)
        {
            return db.MarketOrderRecords
                .AsNoTracking()
                .Where(o => o.SellerId == playerId && o.Status == 0)
                .OrderByDescending(o => o.CreatedAtEpoch)
                .ThenByDescending(o => o.Id)
                .Take(MaxOwnOrders)
                .ToListAsync();
        }

        public static async Task<MarketBrowsePage> BrowseActiveListingsAsync(FolkIdleDbContext db, MarketBrowseQuery query)
        {
            var rows = db.MarketOrderRecords
                .AsNoTracking()
                .Where(o => o.Status == 0
                    && o.OrderType == "SELL"
                    && o.IsQuarantined == query.IsQuarantined
                    && o.QualityTier >= query.MinQualityTier
                    && o.QualityTier <= query.MaxQualityTier);

            if (!string.IsNullOrWhiteSpace(query.BaseItemId))
            {
                string needle = query.BaseItemId.Trim();
                rows = rows.Where(o => EF.Functions.ILike(o.BaseItemId, "%" + needle + "%"));
            }

            // The slot and tier filters are the "helmet / leggings / melee
            // weapon" and "which location's gear" axes the browser is built
            // around. Neither can be expressed in SQL: ResolveSlotIndex is an
            // ordered sequence of substring tests whose ORDER is the contract,
            // and RegionTier lives in the content catalogue rather than on the
            // order row. So both are applied in memory, over a bounded superset
            // rather than the whole book.
            bool filtersInMemory = query.SlotIndices.Count > 0 || query.RegionTiers.Count > 0;
            if (filtersInMemory)
            {
                var candidates = await rows
                    .OrderBy(o => o.Price)
                    .ThenBy(o => o.CreatedAtEpoch)
                    .Take(MaxSlotFilterScan)
                    .ToListAsync();

                var matching = new System.Collections.Generic.List<MarketOrderRecord>(candidates.Count);
                for (int i = 0; i < candidates.Count; i++)
                {
                    MarketOrderRecord candidate = candidates[i];

                    if (query.SlotIndices.Count > 0
                        && !query.SlotIndices.Contains(Domain.Combat.EquipmentSlotEngine.ResolveSlotIndex(candidate.BaseItemId)))
                    {
                        continue;
                    }

                    if (query.RegionTiers.Count > 0
                        && !query.RegionTiers.Contains(ContentRegistry.GetRegionTierForBaseId(candidate.BaseItemId)))
                    {
                        continue;
                    }

                    matching.Add(candidate);
                }

                SortInMemory(matching, query);
                return new MarketBrowsePage
                {
                    TotalCount = matching.Count,
                    Listings = matching
                        .Skip(query.PageIndex * query.PageSize)
                        .Take(query.PageSize)
                        .ToList(),
                };
            }

            int total = await rows.CountAsync();

            // Ordering stays deterministic whatever the sort key: CreatedAtEpoch
            // is always the final tiebreak, so page N does not reshuffle between
            // requests as unrelated listings are created and filled.
            rows = query.SortBy switch
            {
                "rarity" => query.Descending
                    ? rows.OrderByDescending(o => o.QualityTier).ThenBy(o => o.Price).ThenBy(o => o.CreatedAtEpoch)
                    : rows.OrderBy(o => o.QualityTier).ThenBy(o => o.Price).ThenBy(o => o.CreatedAtEpoch),
                "name" => query.Descending
                    ? rows.OrderByDescending(o => o.BaseItemId).ThenBy(o => o.Price).ThenBy(o => o.CreatedAtEpoch)
                    : rows.OrderBy(o => o.BaseItemId).ThenBy(o => o.Price).ThenBy(o => o.CreatedAtEpoch),
                _ => query.Descending
                    ? rows.OrderByDescending(o => o.Price).ThenBy(o => o.CreatedAtEpoch)
                    : rows.OrderBy(o => o.Price).ThenBy(o => o.CreatedAtEpoch),
            };

            return new MarketBrowsePage
            {
                TotalCount = total,
                Listings = await rows
                    .Skip(query.PageIndex * query.PageSize)
                    .Take(query.PageSize)
                    .ToListAsync(),
            };
        }

        // How deep the slot filter reads before giving up. Large enough that a
        // real order book is covered whole, small enough that it can never pull
        // the entire table into memory.
        private const int MaxSlotFilterScan = 2000;

        private static void SortInMemory(System.Collections.Generic.List<MarketOrderRecord> rows, MarketBrowseQuery query)
        {
            System.Comparison<MarketOrderRecord> comparison = query.SortBy switch
            {
                "rarity" => (a, b) => a.QualityTier != b.QualityTier
                    ? a.QualityTier.CompareTo(b.QualityTier)
                    : a.Price.CompareTo(b.Price),
                "name" => (a, b) => string.CompareOrdinal(a.BaseItemId, b.BaseItemId) != 0
                    ? string.CompareOrdinal(a.BaseItemId, b.BaseItemId)
                    : a.Price.CompareTo(b.Price),
                _ => (a, b) => a.Price.CompareTo(b.Price),
            };

            rows.Sort((a, b) =>
            {
                int primary = comparison(a, b);
                if (primary != 0) return query.Descending ? -primary : primary;
                return a.CreatedAtEpoch.CompareTo(b.CreatedAtEpoch);
            });
        }

        public async Task PlaceLimitOrderAsync(long playerId, bool isBuy, long instanceId, long price, string baseItemId, int qualityTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            // Modul: no price corridor (owner, 2026-10-07) - only the arithmetic's
            // own fence, shared with direct listings.
            if (price < MarketEscrowEngine.MinListingPrice || price > MarketEscrowEngine.MaxListingPrice)
            {
                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
                return;
            }

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                if (isBuy)
                {
                    var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                    var goldRecord = await db.CommodityRecords.FromSqlRaw(goldQuery, playerId).SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < price)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                        Console.WriteLine("BUY Order failed: Insufficient gold.");
                        return;
                    }

                    var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
                    bool isQuarantined = (player?.Quarantine_Active ?? false) || (player?.IsQuarantined ?? false);

                    // GoldLedger: escrow, not a spend - a cancel refunds it, so
                    // the spend is recorded at the match, at the price paid.
                    goldRecord.Quantity -= price;

                    var order = new MarketOrderRecord
                    {
                        SellerId = playerId,
                        OrderType = "BUY",
                        BaseItemId = baseItemId,
                        QualityTier = qualityTier,
                        Price = price,
                        Status = 0,
                        IsQuarantined = isQuarantined,
                        CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    db.MarketOrderRecords.Add(order);
                }
                else
                {
                    var equipQuery = "SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                    var equip = await db.MarketEquipmentInstances.FromSqlRaw(equipQuery, instanceId).SingleOrDefaultAsync();

                    if (equip == null || equip.PlayerId != playerId || equip.IsLockedInEscrow)
                    {
                        await transaction.RollbackAsync();
                        _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                        Console.WriteLine("SELL Order failed: Item unavailable or already locked.");
                        return;
                    }

                    var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
                    bool isQuarantined = (player?.Quarantine_Active ?? false) || (player?.IsQuarantined ?? false);

                    baseItemId = equip.BaseItemId;
                    qualityTier = equip.QualityTier;

                    equip.IsLockedInEscrow = true;
                    equip.IsQuarantined = isQuarantined;

                    var order = new MarketOrderRecord
                    {
                        SellerId = playerId,
                        OrderType = "SELL",
                        EquipmentInstanceId = instanceId,
                        BaseItemId = equip.BaseItemId,
                        QualityTier = equip.QualityTier,
                        Price = price,
                        Status = 0,
                        IsQuarantined = isQuarantined,
                        CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                    };
                    db.MarketOrderRecords.Add(order);
                }

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
                Console.WriteLine($"Order placed: {(isBuy ? "BUY" : "SELL")} {baseItemId} T{qualityTier} @ {price}g");

                _ = MatchOrdersAsync(baseItemId, qualityTier);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure);
                Console.WriteLine($"Order placement failed: {ex.Message}");
            }
        }

        public async Task MatchOrdersAsync(string baseItemId, int qualityTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            // Display credits for online players, sent only once the match commits.
            var displayCredits = new List<MarketMatchNotification>();
            try
            {
                var buyQuery = "SELECT * FROM \"MarketOrderRecords\" WHERE \"Status\" = 0 AND \"OrderType\" = 'BUY' AND \"BaseItemId\" = {0} AND \"QualityTier\" = {1} ORDER BY \"Price\" DESC FOR UPDATE";
                var sellQuery = "SELECT * FROM \"MarketOrderRecords\" WHERE \"Status\" = 0 AND \"OrderType\" = 'SELL' AND \"BaseItemId\" = {0} AND \"QualityTier\" = {1} ORDER BY \"Price\" ASC FOR UPDATE";

                var buyOrders = await db.MarketOrderRecords.FromSqlRaw(buyQuery, baseItemId, qualityTier).ToListAsync();
                var sellOrders = await db.MarketOrderRecords.FromSqlRaw(sellQuery, baseItemId, qualityTier).ToListAsync();

                foreach (var buy in buyOrders)
                {
                    var sell = sellOrders.FirstOrDefault(s => s.Status == 0 && s.Price <= buy.Price && s.IsQuarantined == buy.IsQuarantined);
                    if (sell != null)
                    {
                        long executionPrice = sell.Price;
                        // Determine seller's wealth for tax bracket
                        var sellerGold = await db.CommodityRecords.FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", sell.SellerId).SingleOrDefaultAsync();
                        long sellerWealth = sellerGold?.Quantity ?? 0;
                        
                        // Modul 40/51: wealth-scaled silver-sink tax burn.
                        double totalFeeRate = 0.05;
                        if (sellerWealth > 5000000) totalFeeRate = 0.15;
                        else if (sellerWealth >= 500000) totalFeeRate = 0.08;
                        
                        long fee = (long)(executionPrice * totalFeeRate);
                        long sellerProceeds = executionPrice - fee;
                        long refundToBuyer = buy.Price - executionPrice;

                        // Task 79: the buyer's escrow became a purchase at the
                        // execution price; the rest comes back below.
                        await GoldLedger.RecordSpendAsync(db, buy.SellerId, GoldSpendCategory.Market, executionPrice);

                        // Transfer equipment (Always safe to DB write as item tables are not flushed via standard tick cache)
                        var equip = await db.MarketEquipmentInstances.FromSqlRaw("SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE", (object)(sell.EquipmentInstanceId ?? 0)).SingleAsync();
                        equip.PlayerId = buy.SellerId; 
                        equip.IsLockedInEscrow = false;

                        // Task 79: the seller's income, whichever branch pays it.
                        await GoldLedger.RecordIncomeAsync(db, sell.SellerId, GoldIncomeSource.Market, sellerProceeds);

                        // Modul: THE ROWS, ONLINE OR NOT (2026-09-30). Both
                        // online branches used to post to MarketMatchQueue
                        // only, whose drain moves CurrentGold and nothing
                        // else - and nothing persists CurrentGold, so an online
                        // seller's proceeds and an online buyer's refund
                        // vanished at the next relogin. Each row is credited
                        // here, in the match's transaction; an online player's
                        // display moves after the commit (the chest-sale path
                        // of CLAUDE.md's "two gold paths").
                        // The upsert rebases the tracked sellerGold row read above.
                        // GoldLedger: recorded as Market income above.
                        await CommodityLedger.AddAsync(db, sell.SellerId, "gold", sellerProceeds);
                        if (_playerRegistry.IsPlayerOnline(sell.SellerId))
                        {
                            displayCredits.Add(new MarketMatchNotification
                            {
                                PlayerId = sell.SellerId,
                                GoldDelta = sellerProceeds,
                                NewEquipmentInstanceId = null
                            });
                        }

                        if (refundToBuyer > 0)
                        {
                            // Modul: this used to skip the refund outright when
                            // the buyer had no gold row (`if (buyerGold != null)`),
                            // so an offline buyer whose row was missing lost it
                            // in silence. The upsert creates the row (task 44).
                            // GoldLedger: the unused part of the buyer's own escrow, not income.
                            await CommodityLedger.AddAsync(db, buy.SellerId, "gold", refundToBuyer);
                        }
                        if (_playerRegistry.IsPlayerOnline(buy.SellerId))
                        {
                            displayCredits.Add(new MarketMatchNotification
                            {
                                PlayerId = buy.SellerId,
                                GoldDelta = refundToBuyer,
                                NewEquipmentInstanceId = sell.EquipmentInstanceId
                            });
                        }

                        // Archive matching order
                        var archive = new HistoricalMarketArchive
                        {
                            OriginalOrderId = sell.Id,
                            SellerId = sell.SellerId,
                            BuyerId = buy.SellerId,
                            CommodityId = sell.CommodityId,
                            EquipmentInstanceId = sell.EquipmentInstanceId,
                            ExecutionPrice = executionPrice,
                            FeeBurned = fee,
                            OrderType = "MATCH",
                            BaseItemId = sell.BaseItemId,
                            QualityTier = sell.QualityTier,
                            IsQuarantined = sell.IsQuarantined,
                            ExecutionTimestampEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                        };
                        
                        db.HistoricalMarketArchives.Add(archive);
                        await db.SaveChangesAsync(); // Explicitly flush to avoid FK constraint issues during eviction
                        
                        // Evict active ledger rows
                        db.MarketOrderRecords.Remove(buy);
                        db.MarketOrderRecords.Remove(sell);

                        await db.SaveChangesAsync();
                        Console.WriteLine($"Matched Order! {baseItemId} sold for {executionPrice}g.");
                        sell.Status = 1; // Prevent matching same row within memory iteration
                    }
                }

                await transaction.CommitAsync();
                // After the commit, never inside it: a display credit for a
                // match that then rolled back would be gold the row never got.
                foreach (var credit in displayCredits)
                {
                    _playerRegistry.MarketMatchQueue.Enqueue(credit);
                }
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Order matching failed: {ex.Message}");
            }
        }
    }
}
