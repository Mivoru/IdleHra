using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using System.Data;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Economy
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

        // Modul: retryable listing. The outcome of ONE attempt. The delegate
        // handed to an execution strategy may run more than once, so anything
        // that must happen exactly once - pushing a command result to the
        // player, writing a log line - has to sit outside it, keyed off this.
        // Same shape as EquipmentSlotEngine.EquipAttemptOutcome, and for the
        // same reason.
        private readonly struct ListAttemptOutcome
        {
            public readonly bool Listed;
            public readonly byte? ResultCode;
            public readonly string? LogMessage;

            public ListAttemptOutcome(bool listed, byte? resultCode, string? logMessage)
            {
                Listed = listed;
                ResultCode = resultCode;
                LogMessage = logMessage;
            }

            public static ListAttemptOutcome Rejected(string logMessage, byte? resultCode = null)
                => new ListAttemptOutcome(false, resultCode, logMessage);
        }

        // Modul: retryable listing. Wrapped in an execution strategy because
        // this is a Serializable transaction that several callers can run
        // concurrently for the same player, and losing the serialization race
        // is normal and recoverable - not a reason to tell the player their
        // listing failed.
        //
        // Before this, a 40001 surfaced as "An exception has been raised that
        // is likely due to a transient failure", was swallowed by the catch-all
        // below, and returned false. Under real concurrent load that meant five
        // of six simultaneous listings were silently dropped;
        // Test_MarketEscrow_ConcurrentListings_ExactReplicaNoSerializationDrift
        // has been failing on that for as long as it has existed, and it was
        // right to.
        //
        // Uses RetryingDbContextOptions rather than the scoped context for the
        // reason EquipmentSlotEngine documents: EF refuses user-initiated
        // transactions under a retrying strategy unless the context was built
        // with one.
        public async Task<bool> ListItemAsync(long playerId, long instanceId, long limitPrice)
        {
            await using var db = new FolkIdleDbContext(_serviceProvider.GetRequiredService<RetryingDbContextOptions>().Options);
            var strategy = db.Database.CreateExecutionStrategy();

            ListAttemptOutcome outcome;
            try
            {
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    // A retry must not inherit a half-applied graph from the
                    // attempt that just lost the race.
                    db.ChangeTracker.Clear();
                    return await AttemptListAsync(db, playerId, instanceId, limitPrice);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"MarketListItem failed: {ex.Message}");
                return false;
            }

            if (outcome.LogMessage != null)
            {
                Console.WriteLine(outcome.LogMessage);
            }
            if (outcome.ResultCode.HasValue)
            {
                _playerRegistry.EnqueueCommandResult(playerId, outcome.ResultCode.Value);
            }

            return outcome.Listed;
        }

        private async Task<ListAttemptOutcome> AttemptListAsync(FolkIdleDbContext db, long playerId, long instanceId, long limitPrice)
        {
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            {
                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.RollbackAsync();
                    return ListAttemptOutcome.Rejected("MarketListItem failed: Player not found.");
                }

                // Modul: Advanced Economy Refactoring, Part 2.1. Trade
                // license - global market access requires an active guild
                // membership. Checked before any row mutation, mirroring
                // the equipped-item guard's early-abort pattern below.
                if (player.GuildId <= 0)
                {
                    await transaction.RollbackAsync();
                    return ListAttemptOutcome.Rejected(
                        "MarketListItem failed: Player has no guild trade license.",
                        (byte)FolkIdle.Server.Network.CommandResultCode.NoGuildLicense);
                }

                var equipQuery = "SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var equip = await db.EquipmentInstances.FromSqlRaw(equipQuery, instanceId).SingleOrDefaultAsync();

                if (equip == null || equip.PlayerId != playerId)
                {
                    await transaction.RollbackAsync();
                    return ListAttemptOutcome.Rejected(
                        "MarketListItem failed: Item unavailable.",
                        (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
                }

                // Modul 04/40: an item currently equipped on the character
                // cannot be migrated into escrow out from under it - abort
                // before any row mutation happens.
                // Modul: per-character equipment. Was a three-field compare on
                // the player row. Equipment now lives on characters, so an item
                // worn by ANY of them must be unlistable - otherwise a player
                // could sell the sword their second character is holding and
                // leave a dangling equip pointer behind.
                if (await EquipmentSlotEngine.IsEquippedAnywhereAsync(db, playerId, equip.Id))
                {
                    await transaction.RollbackAsync();
                    return ListAttemptOutcome.Rejected(
                        "MarketListItem failed: Item is currently equipped.",
                        (byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped);
                }

                // Modul 40/51: strict 20%-to-300% volatility corridor against
                // the 7-day rolling average, falling back to a deterministic
                // ContentRegistry baseline for untraded items so this direct
                // listing path cannot be used to launder gold via an
                // arbitrarily priced never-before-traded item.
                //
                // Modul: AN ITEM WITH NO PRICE IS NOT AN ITEM WITH NO LIMIT.
                // This ran the corridor only when a price could be computed and
                // let everything else straight through - which was harmless
                // while every item in the database was in the catalogue, and
                // stopped being harmless the moment the catalogue was cut from
                // 437 entries to its 75 canonical pieces. Every instance of a
                // removed item still sits in EquipmentInstances, has no
                // baseline, has never traded, and could therefore be listed at
                // any price at all: exactly the laundering route the corridor
                // exists to close, handed out by a content change.
                //
                // Fails closed. An item nobody can price is an item nobody can
                // sell, which costs a player nothing they can name - the pieces
                // this rejects cannot be equipped or valued either.
                double? rollingAveragePrice = await MarketOrderBookEngine.CalculateRollingAveragePriceAsync(db, equip.BaseItemId, equip.QualityTier);
                if (!rollingAveragePrice.HasValue)
                {
                    await transaction.RollbackAsync();
                    return ListAttemptOutcome.Rejected(
                        $"MarketListItem rejected: no price baseline for {equip.BaseItemId} - not in the catalogue and never traded.",
                        (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
                }

                {
                    double minPrice = rollingAveragePrice.Value * 0.80;
                    double maxPrice = rollingAveragePrice.Value * 3.00;
                    if (limitPrice < minPrice || limitPrice > maxPrice)
                    {
                        await transaction.RollbackAsync();
                        return ListAttemptOutcome.Rejected(
                            $"MarketListItem rejected: price {limitPrice} outside volatility corridor [{minPrice}, {maxPrice}] for {equip.BaseItemId} T{equip.QualityTier}.",
                            (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
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

                return new ListAttemptOutcome(
                    true,
                    (byte)FolkIdle.Server.Network.CommandResultCode.Success,
                    $"Direct Listing: Item {instanceId} listed by Player {playerId} for {limitPrice}g.");
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

                // Modul: Advanced Economy Refactoring, Part 2.1. Trade
                // license - buying requires guild membership, matching
                // ListItemAsync's own gate.
                if (buyer == null || buyer.GuildId <= 0)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine("MarketBuyItem failed: Buyer has no guild trade license.");
                    _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.NoGuildLicense);
                    return;
                }

                // Modul: region gate at the purchase, matching the one at equip
                // time - a buyer who has not opened the item's region cannot
                // buy it at all, closing the "buy ahead of your progress and
                // coast" loop at the shop front rather than only when the item
                // is worn.
                //
                // Was a level lock keyed on RegionTier AND QualityTier. It moved
                // with EquipmentSlotEngine's and for the same reason: leaving
                // one end asking about levels and rarity while the other asked
                // about bosses would have let a player buy gear the equip path
                // then refused, which is a worse failure than either rule alone
                // - the gold is gone and the item is dead weight.
                var buyerDefeatedBosses = await RegionUnlockGate.LoadDefeatedBossesAsync(db, buyerId);
                if (!RegionUnlockGate.CanWearItem(order.BaseItemId, buyerDefeatedBosses))
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"MarketBuyItem failed: buyer {buyerId} has not unlocked the region for {order.BaseItemId} (highest unlocked {RegionUnlockGate.HighestUnlockedRegion(buyerDefeatedBosses)}).");
                    _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.RegionLocked);
                    return;
                }

                var goldQuery = "SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = 'gold' FOR UPDATE";
                var buyerGold = await db.CommodityRecords.FromSqlRaw(goldQuery, buyerId).SingleOrDefaultAsync();

                if (buyerGold == null || buyerGold.Quantity < order.Price)
                {
                    Console.WriteLine("MarketBuyItem failed: Insufficient gold.");
                    _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.InsufficientGold);
                    return;
                }

                var equipQuery = "SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE";
                var equip = await db.MarketEquipmentInstances.FromSqlRaw(equipQuery, (object)(order.EquipmentInstanceId ?? 0)).SingleOrDefaultAsync();

                if (equip == null)
                {
                    Console.WriteLine("MarketBuyItem failed: Equipment not found.");
                    _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.TargetNotFound);
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

                // Modul: Advanced Economy Refactoring, Part 2.5. Guild
                // sales tax - the SELLER's guild takes its configured
                // TaxRatePct cut of the gross price, deposited into that
                // guild's central gold ledger row (the same
                // GuildMaterialSinkLedger gold row donations flow into),
                // and only the net remainder reaches the seller. The
                // seller had a guild at listing time (trade license), but
                // may have left since - in that case no guild tax applies,
                // matching the license's own semantics (no guild, no
                // market participation, no tax relationship).
                long guildTax = 0L;
                var sellerRecord = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", order.SellerId)
                    .SingleOrDefaultAsync();
                if (sellerRecord != null && sellerRecord.GuildId > 0)
                {
                    var sellerGuild = await db.GuildRecords
                        .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", sellerRecord.GuildId)
                        .SingleOrDefaultAsync();
                    if (sellerGuild != null)
                    {
                        int taxRatePct = Math.Clamp(sellerGuild.TaxRatePct, GuildRecord.MinTaxRatePct, GuildRecord.MaxTaxRatePct);
                        guildTax = executionPrice * taxRatePct / 100L;

                        if (guildTax > 0L)
                        {
                            var guildGoldLedger = await db.GuildMaterialSinkLedgers
                                .FromSqlRaw("SELECT * FROM \"GuildMaterialSinkLedgers\" WHERE \"GuildId\" = {0} AND \"CommodityId\" = 'gold' FOR UPDATE", sellerRecord.GuildId)
                                .SingleOrDefaultAsync();
                            if (guildGoldLedger == null)
                            {
                                guildGoldLedger = new GuildMaterialSinkLedger { GuildId = sellerRecord.GuildId, CommodityId = "gold", TotalAmountContributed = 0 };
                                db.GuildMaterialSinkLedgers.Add(guildGoldLedger);
                            }
                            guildGoldLedger.TotalAmountContributed += guildTax;
                        }
                    }
                }

                long sellerProceeds = executionPrice - fee - guildTax;

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
                _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"MarketBuyItem failed: {ex.Message}");
            }
        }
    }
}
