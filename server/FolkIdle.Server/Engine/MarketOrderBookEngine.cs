using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using System.Data;

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

        public async Task PlaceLimitOrderAsync(long playerId, bool isBuy, long instanceId, long price, string baseItemId, int qualityTier)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

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
                        IsQuarantined = isQuarantined
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

                    equip.IsLockedInEscrow = true;
                    equip.IsQuarantined = isQuarantined;
                    baseItemId = equip.BaseItemId;
                    qualityTier = equip.QualityTier;

                    var order = new MarketOrderRecord
                    {
                        SellerId = playerId,
                        OrderType = "SELL",
                        EquipmentInstanceId = instanceId,
                        BaseItemId = equip.BaseItemId,
                        QualityTier = equip.QualityTier,
                        Price = price,
                        Status = 0,
                        IsQuarantined = isQuarantined
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
                        
                        double totalFeeRate = 0.06;
                        if (sellerWealth > 5000000) totalFeeRate = 0.18;
                        else if (sellerWealth >= 500000) totalFeeRate = 0.10;
                        
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
