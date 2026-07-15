using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using System.Data;

namespace FolkIdle.Server.Engine
{
    public class MarketEscrowEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public MarketEscrowEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task<bool> ListItemAsync(long playerId, long instanceId, long limitPrice)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("MarketListItem failed: Player not found.");
                    return false;
                }

                var equipQuery = "SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var equip = await db.EquipmentInstances.FromSqlRaw(equipQuery, instanceId).SingleOrDefaultAsync();

                if (equip == null || equip.PlayerId != playerId)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("MarketListItem failed: Item unavailable.");
                    return false;
                }

                // Modul 04/40: an item currently equipped on the character
                // cannot be migrated into escrow out from under it - abort
                // before any row mutation happens.
                if (player.EquippedWeaponId == equip.Id || player.EquippedArmorId == equip.Id)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("MarketListItem failed: Item is currently equipped.");
                    return false;
                }

                // Modul 40/51: strict 20%-to-300% volatility corridor against
                // the 7-day rolling average, falling back to a deterministic
                // ContentRegistry baseline for untraded items so this direct
                // listing path cannot be used to launder gold via an
                // arbitrarily priced never-before-traded item.
                double? rollingAveragePrice = await MarketOrderBookEngine.CalculateRollingAveragePriceAsync(db, equip.BaseItemId, equip.QualityTier);
                if (rollingAveragePrice.HasValue)
                {
                    double minPrice = rollingAveragePrice.Value * 0.80;
                    double maxPrice = rollingAveragePrice.Value * 3.00;
                    if (limitPrice < minPrice || limitPrice > maxPrice)
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"MarketListItem rejected: price {limitPrice} outside volatility corridor [{minPrice}, {maxPrice}] for {equip.BaseItemId} T{equip.QualityTier}.");
                        return false;
                    }
                }

                bool isQuarantined = player.Quarantine_Active || player.IsQuarantined;

                db.EquipmentInstances.Remove(equip);
                var marketEquip = new MarketEquipmentInstance
                {
                    PlayerId = playerId,
                    BaseItemId = equip.BaseItemId,
                    QualityTier = equip.QualityTier,
                    AffixPayload = equip.AffixPayload,
                    IsAffixLocked = equip.IsAffixLocked,
                    IsLockedInEscrow = true,
                    IsQuarantined = isQuarantined
                };
                db.MarketEquipmentInstances.Add(marketEquip);
                await db.SaveChangesAsync(); // generate new id for market equipment

                var order = new MarketOrderRecord
                {
                    SellerId = playerId,
                    OrderType = "SELL",
                    EquipmentInstanceId = marketEquip.Id,
                    BaseItemId = equip.BaseItemId,
                    QualityTier = equip.QualityTier,
                    Price = limitPrice,
                    Status = 0,
                    IsQuarantined = isQuarantined,
                    CreatedAtEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                db.MarketOrderRecords.Add(order);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                Console.WriteLine($"Direct Listing: Item {instanceId} listed by Player {playerId} for {limitPrice}g.");
                return true;
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"MarketListItem failed: {ex.Message}");
                return false;
            }
        }

        public async Task BuyItemAsync(long buyerId, long orderId, bool hasSpace)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var orderQuery = "SELECT * FROM \"MarketOrderRecords\" WHERE \"Id\" = {0} AND \"Status\" = 0 AND \"OrderType\" = 'SELL' FOR UPDATE";
                var order = await db.MarketOrderRecords.FromSqlRaw(orderQuery, orderId).SingleOrDefaultAsync();

                if (order == null)
                {
                    Console.WriteLine("MarketBuyItem failed: Order not found or already filled.");
                    return;
                }

                if (order.SellerId == buyerId)
                {
                    Console.WriteLine("MarketBuyItem failed: Cannot buy your own item.");
                    return;
                }

                var buyer = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", buyerId)
                    .SingleOrDefaultAsync();
                bool buyerQuarantined = (buyer?.Quarantine_Active ?? false) || (buyer?.IsQuarantined ?? false);

                if (buyerQuarantined != order.IsQuarantined)
                {
                    Console.WriteLine("MarketBuyItem failed: Isolated market mismatch.");
                    return;
                }

                var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                var buyerGold = await db.CommodityRecords.FromSqlRaw(goldQuery, buyerId).SingleOrDefaultAsync();

                if (buyerGold == null || buyerGold.Quantity < order.Price)
                {
                    Console.WriteLine("MarketBuyItem failed: Insufficient gold.");
                    return;
                }

                var equipQuery = "SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var equip = await db.MarketEquipmentInstances.FromSqlRaw(equipQuery, (object)(order.EquipmentInstanceId ?? 0)).SingleOrDefaultAsync();

                if (equip == null)
                {
                    Console.WriteLine("MarketBuyItem failed: Equipment not found.");
                    return;
                }

                if (equip.IsQuarantined != buyerQuarantined)
                {
                    Console.WriteLine("MarketBuyItem failed: Equipment isolation mismatch.");
                    return;
                }

                buyerGold.Quantity -= order.Price;

                if (hasSpace)
                {
                    // Transfer the item back to the buyer's active inventory (MarketEquipmentInstance holds it)
                    // Wait, we need to move it to EquipmentInstances if the active inventory is there!
                    // Let's remove from MarketEquipmentInstances and add to EquipmentInstances
                    db.MarketEquipmentInstances.Remove(equip);
                    var newEquip = new EquipmentInstance
                    {
                        PlayerId = buyerId,
                        BaseItemId = equip.BaseItemId,
                        QualityTier = equip.QualityTier,
                        AffixPayload = equip.AffixPayload,
                        IsAffixLocked = equip.IsAffixLocked
                    };
                    db.EquipmentInstances.Add(newEquip);
                }
                else
                {
                    // Fallback to Mailbox
                    db.MarketEquipmentInstances.Remove(equip);
                    var newEquip = new EquipmentInstance
                    {
                        PlayerId = buyerId,
                        BaseItemId = equip.BaseItemId,
                        QualityTier = equip.QualityTier,
                        AffixPayload = equip.AffixPayload,
                        IsAffixLocked = equip.IsAffixLocked
                    };
                    db.EquipmentInstances.Add(newEquip);
                    await db.SaveChangesAsync(); // Save to get the ID

                    var count = await db.MailboxInstances.FromSqlInterpolated($"SELECT * FROM \"MailboxInstances\" WHERE \"PlayerId\" = {buyerId} FOR UPDATE").CountAsync();

                    if (count < 50)
                    {
                        var mail = new MailboxInstance
                        {
                            PlayerId = buyerId,
                            BaseItemId = equip.BaseItemId,
                            QualityTier = equip.QualityTier,
                            Quantity = 1,
                            IsClaimed = false,
                            IsPending = false,
                            GoldAttachment = 0,
                            AttachedEquipmentId = newEquip.Id,
                            ReceivedTimestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        };
                        db.MailboxInstances.Add(mail);
                    }
                    else
                    {
                        // Vaporize overflow
                    }
                }

                long executionPrice = order.Price;
                
                // Determine seller's wealth for tax bracket
                var sellerGold = await db.CommodityRecords.FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE", order.SellerId).SingleOrDefaultAsync();
                long sellerWealth = sellerGold?.Quantity ?? 0;
                
                // Modul 40/51: wealth-scaled silver-sink tax burn, matching
                // MarketOrderBookEngine.MatchOrdersAsync's brackets.
                double totalFeeRate = 0.05;
                if (sellerWealth > 5000000) totalFeeRate = 0.15;
                else if (sellerWealth >= 500000) totalFeeRate = 0.08;
                
                long fee = (long)(executionPrice * totalFeeRate);
                long sellerProceeds = executionPrice - fee;

                if (_playerRegistry.IsPlayerOnline(order.SellerId))
                {
                    _playerRegistry.MarketMatchQueue.Enqueue(new MarketMatchNotification
                    {
                        PlayerId = order.SellerId,
                        GoldDelta = sellerProceeds,
                        NewEquipmentInstanceId = null // Seller doesn't get a new equipment
                    });
                }
                else
                {
                    if (sellerGold == null)
                    {
                        sellerGold = new CommodityRecord { PlayerId = order.SellerId, ItemId = "gold", Quantity = 0 };
                        db.CommodityRecords.Add(sellerGold);
                    }
                    sellerGold.Quantity += sellerProceeds;
                }

                // Archive matching order
                var archive = new HistoricalMarketArchive
                {
                    OriginalOrderId = order.Id,
                    SellerId = order.SellerId,
                    BuyerId = buyerId,
                    CommodityId = order.CommodityId,
                    EquipmentInstanceId = order.EquipmentInstanceId,
                    ExecutionPrice = executionPrice,
                    FeeBurned = fee,
                    OrderType = "MATCH",
                    BaseItemId = order.BaseItemId,
                    QualityTier = order.QualityTier,
                    IsQuarantined = order.IsQuarantined,
                    ExecutionTimestampEpoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                };
                
                db.HistoricalMarketArchives.Add(archive);
                await db.SaveChangesAsync(); // Explicitly flush to avoid FK constraint issues during eviction
                
                // Evict active ledger row
                db.MarketOrderRecords.Remove(order);

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                Console.WriteLine($"Direct Buy: Order {orderId} purchased by {buyerId} for {order.Price}g.");
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"MarketBuyItem failed: {ex.Message}");
            }
        }
    }
}
