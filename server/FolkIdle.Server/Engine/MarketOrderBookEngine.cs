using System;
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

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                if (isBuy)
                {
                    // Modul 40/51: strict 20%-to-300% volatility corridor
                    // (P_min = P_avg * 0.80, P_max = P_avg * 3.00), computed
                    // from real completed-order history. baseItemId is already
                    // the real item identity for a BUY order at this point.
                    //
                    // Modul: fails closed on an unpriceable item, the same way
                    // the direct SELL path in MarketEscrowEngine now does and
                    // for the same reason - a BUY order at an arbitrary price
                    // moves gold between two players just as effectively as a
                    // SELL does. There is no baseline only when the item is
                    // absent from the catalogue AND has never traded, which
                    // after the catalogue cut describes every legacy piece.
                    double? buyRollingAveragePrice = await CalculateRollingAveragePriceAsync(db, baseItemId, qualityTier);
                    if (!buyRollingAveragePrice.HasValue)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"BUY Order rejected: no price baseline for {baseItemId} - not in the catalogue and never traded.");
                        return;
                    }

                    {
                        double buyMinPrice = buyRollingAveragePrice.Value * 0.80;
                        double buyMaxPrice = buyRollingAveragePrice.Value * 3.00;
                        if (price < buyMinPrice || price > buyMaxPrice)
                        {
                            await transaction.RollbackAsync();
                            Console.WriteLine($"BUY Order rejected: price {price} outside volatility corridor [{buyMinPrice}, {buyMaxPrice}] for {baseItemId} T{qualityTier}.");
                            return;
                        }
                    }

                    var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                    var goldRecord = await db.CommodityRecords.FromSqlRaw(goldQuery, playerId).SingleOrDefaultAsync();

                    if (goldRecord == null || goldRecord.Quantity < price)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine("BUY Order failed: Insufficient gold.");
                        return;
                    }

                    var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
                    bool isQuarantined = (player?.Quarantine_Active ?? false) || (player?.IsQuarantined ?? false);

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
                        Console.WriteLine("SELL Order failed: Item unavailable or already locked.");
                        return;
                    }

                    var player = await db.PlayerRecords.FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId).SingleOrDefaultAsync();
                    bool isQuarantined = (player?.Quarantine_Active ?? false) || (player?.IsQuarantined ?? false);

                    baseItemId = equip.BaseItemId;
                    qualityTier = equip.QualityTier;

                    // Modul 40/51: strict 20%-to-300% volatility corridor,
                    // checked here (not before the transaction) since the
                    // caller does not know the real item identity for a SELL
                    // order until the equipment row above is resolved.
                    double? sellRollingAveragePrice = await CalculateRollingAveragePriceAsync(db, baseItemId, qualityTier);
                    if (sellRollingAveragePrice.HasValue)
                    {
                        double sellMinPrice = sellRollingAveragePrice.Value * 0.80;
                        double sellMaxPrice = sellRollingAveragePrice.Value * 3.00;
                        if (price < sellMinPrice || price > sellMaxPrice)
                        {
                            await transaction.RollbackAsync();
                            Console.WriteLine($"SELL Order rejected: price {price} outside volatility corridor [{sellMinPrice}, {sellMaxPrice}] for {baseItemId} T{qualityTier}.");
                            return;
                        }
                    }

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

                Console.WriteLine($"Order placed: {(isBuy ? "BUY" : "SELL")} {baseItemId} T{qualityTier} @ {price}g");

                _ = MatchOrdersAsync(baseItemId, qualityTier);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Order placement failed: {ex.Message}");
            }
        }

        public async Task MatchOrdersAsync(string baseItemId, int qualityTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
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

                        // Transfer equipment (Always safe to DB write as item tables are not flushed via standard tick cache)
                        var equip = await db.MarketEquipmentInstances.FromSqlRaw("SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE", (object)(sell.EquipmentInstanceId ?? 0)).SingleAsync();
                        equip.PlayerId = buy.SellerId; 
                        equip.IsLockedInEscrow = false;

                        // Give seller gold
                        if (_playerRegistry.IsPlayerOnline(sell.SellerId))
                        {
                            _playerRegistry.MarketMatchQueue.Enqueue(new MarketMatchNotification
                            {
                                PlayerId = sell.SellerId,
                                GoldDelta = sellerProceeds,
                                NewEquipmentInstanceId = null
                            });
                        }
                        else
                        {
                            if (sellerGold == null)
                            {
                                sellerGold = new CommodityRecord { PlayerId = sell.SellerId, ItemId = "gold", Quantity = 0 };
                                db.CommodityRecords.Add(sellerGold);
                            }
                            sellerGold.Quantity += sellerProceeds;
                        }

                        // Give buyer refund and notification
                        if (_playerRegistry.IsPlayerOnline(buy.SellerId))
                        {
                            _playerRegistry.MarketMatchQueue.Enqueue(new MarketMatchNotification
                            {
                                PlayerId = buy.SellerId,
                                GoldDelta = refundToBuyer,
                                NewEquipmentInstanceId = sell.EquipmentInstanceId
                            });
                        }
                        else if (refundToBuyer > 0)
                        {
                            var buyerGold = await db.CommodityRecords.FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", buy.SellerId).SingleOrDefaultAsync();
                            if (buyerGold != null) buyerGold.Quantity += refundToBuyer;
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
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Order matching failed: {ex.Message}");
            }
        }
    }
}
