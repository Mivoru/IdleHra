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
    /// <summary>
    /// Why a cancel did or did not happen. Sent to the client by name
    /// (<c>POST /api/v1/market/cancel</c>), so a refusal can say why.
    /// </summary>
    public enum MarketCancelResult
    {
        Ok,
        /// <summary>The listing sold before the cancel reached it.</summary>
        Sold,
        /// <summary>No such open order - already cancelled, or filled.</summary>
        Gone,
        NotYours,
        /// <summary>An order whose escrow this path cannot return.</summary>
        Unsupported,
    }

    public readonly record struct MarketCancelOutcome(
        MarketCancelResult Result,
        long? ReturnedEquipmentId = null,
        long RefundedGold = 0);

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

        // Modul: the one price fence for equipment, listing and order book alike.
        // Matches CosmeticRegistry.MaxMarketPrice and the client's input max.
        public const long MinListingPrice = 1;
        public const long MaxListingPrice = 1_000_000_000;

        private async Task<ListAttemptOutcome> AttemptListAsync(FolkIdleDbContext db, long playerId, long instanceId, long limitPrice)
        {
            if (limitPrice < MinListingPrice || limitPrice > MaxListingPrice)
            {
                return ListAttemptOutcome.Rejected(
                    $"MarketListItem rejected: price {limitPrice} outside [{MinListingPrice}, {MaxListingPrice}].",
                    (byte)FolkIdle.Server.Network.CommandResultCode.InvalidPrice);
            }

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

                // Modul: NO PRICE CORRIDOR (owner, 2026-10-07). The 20%-300% corridor
                // against the rolling average refused ordinary listings as "invalid
                // price" and, for an item with no baseline, refused them outright.
                // The seller chooses the price, as for cosmetics; the only fences
                // are the arithmetic's own (1 .. MaxListingPrice), checked below
                // before any row is touched.

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
                await GoldLedger.RecordSpendAsync(db, buyerId, GoldSpendCategory.Market, order.Price);

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
                long fee = (long)(executionPrice * WealthFeeRate(sellerWealth));

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
                long guildTax = await ApplyGuildSalesTaxAsync(db, order.SellerId, executionPrice);

                long sellerProceeds = executionPrice - fee - guildTax;

                // Task 79: the sale is income whichever branch pays it, and it
                // is counted here, in the sale's transaction, never at the
                // tick's display-only AddGold or the rescue.
                await GoldLedger.RecordIncomeAsync(db, order.SellerId, GoldIncomeSource.Market, sellerProceeds);

                // Modul: THE ROW, ONLINE OR NOT (2026-09-30). The online branch
                // used to post the proceeds to MarketMatchQueue only, whose
                // drain moves CurrentGold and nothing else - and nothing
                // persists CurrentGold (the checkpoint banks only
                // RedisPendingGoldDelta; login reloads the row). An online
                // seller's sale vanished at their next relogin. This is now
                // the chest-sale path of CLAUDE.md's "two gold paths": the row
                // is credited here, in the sale's transaction, and the live
                // payload moves CurrentGold ONLY (after the commit, below).
                // The upsert rebases the tracked sellerGold row read above.
                // GoldLedger: recorded as Market income above.
                await CommodityLedger.AddAsync(db, order.SellerId, "gold", sellerProceeds);
                bool showSellerGold = _playerRegistry.IsPlayerOnline(order.SellerId);

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

                // After the commit, never inside it: a display credit for a
                // sale that then rolled back would be gold the row never got.
                if (showSellerGold)
                {
                    _playerRegistry.MarketMatchQueue.Enqueue(new MarketMatchNotification
                    {
                        PlayerId = order.SellerId,
                        GoldDelta = sellerProceeds,
                        NewEquipmentInstanceId = null // Seller doesn't get a new equipment
                    });
                }

                Console.WriteLine($"Direct Buy: Order {orderId} purchased by {buyerId} for {order.Price}g.");
                _playerRegistry.EnqueueCommandResult(buyerId, (byte)FolkIdle.Server.Network.CommandResultCode.Success);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"MarketBuyItem failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Takes one of the caller's own open orders off the book and hands
        /// back what it held in escrow: a SELL listing's piece goes back to
        /// the chest, a BUY order's gold goes back to the gold row. One
        /// transaction; answers why when it refuses.
        /// </summary>
        /// <remarks>
        /// Modul: THE ORDER ROW IS THE LOCK, the same row BuyItemAsync and
        /// MatchOrdersAsync take FOR UPDATE before they move anything. Whoever
        /// holds it first decides the order's fate, and the other side reads it
        /// afterwards:
        ///   - buy first: the sale commits and deletes the row, and this
        ///     SELECT, waiting on the lock, then finds nothing (Read Committed
        ///     re-reads after the wait) - Sold, nothing refunded;
        ///   - cancel first: the row is gone when the buy's own FOR UPDATE
        ///     wakes, and the buy (Serializable) fails with 40001 and rolls
        ///     back - no gold debited, no item granted.
        /// So one press can never both sell and refund. Read Committed rather
        /// than the buy's Serializable on purpose: the losing cancel then
        /// answers "already sold" instead of throwing a serialization error at
        /// a player who did nothing wrong.
        ///
        /// Modul: THE PIECE COMES BACK AS A NEW CHEST ROW, exactly the way a
        /// buyer receives it - listing moved it out of EquipmentInstances into
        /// MarketEquipmentInstances, so its old id no longer exists. Base item,
        /// rarity, affixes and the affix lock are carried over; those are all
        /// the listed copy kept. The chest is unlimited (see
        /// MarketTickCoordinator), so there is no "full chest" branch to take:
        /// a cancel never needs the mailbox fallback, and never vaporises.
        /// </remarks>
        public static async Task<MarketCancelOutcome> CancelOrderAsync(FolkIdleDbContext db, long playerId, long orderId)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);

            var order = await db.MarketOrderRecords
                .FromSqlRaw("SELECT * FROM \"MarketOrderRecords\" WHERE \"Id\" = {0} FOR UPDATE", orderId)
                .SingleOrDefaultAsync();

            if (order == null)
            {
                await transaction.RollbackAsync();
                // Gone. Say whether it sold, so the player is not left
                // wondering where the piece went - a filled listing is
                // archived under its own order id. (A filled BUY order is
                // archived under the SELL it matched, so it reads as Gone.)
                bool sold = await db.HistoricalMarketArchives.AsNoTracking()
                    .AnyAsync(a => a.OriginalOrderId == orderId && a.SellerId == playerId);
                return new MarketCancelOutcome(sold ? MarketCancelResult.Sold : MarketCancelResult.Gone);
            }

            if (order.SellerId != playerId)
            {
                await transaction.RollbackAsync();
                return new MarketCancelOutcome(MarketCancelResult.NotYours);
            }

            if (order.Status != 0)
            {
                await transaction.RollbackAsync();
                return new MarketCancelOutcome(MarketCancelResult.Gone);
            }

            long? returnedEquipmentId = null;
            long refundedGold = 0;

            if (order.OrderType == "BUY")
            {
                // GoldLedger: the buyer's own escrow coming back, not income - PlaceLimitOrderAsync never recorded it as a spend.
                await CommodityLedger.AddAsync(db, playerId, "gold", order.Price);
                refundedGold = order.Price;
                db.MarketOrderRecords.Remove(order);
                await db.SaveChangesAsync();
            }
            else if (order.OrderType == "SELL" && order.EquipmentInstanceId.HasValue)
            {
                var escrowed = await db.MarketEquipmentInstances
                    .FromSqlRaw("SELECT * FROM \"MarketEquipmentInstances\" WHERE \"Id\" = {0} FOR UPDATE", order.EquipmentInstanceId.Value)
                    .SingleOrDefaultAsync();

                if (escrowed != null && escrowed.PlayerId != playerId)
                {
                    // An order that points at someone else's piece is corrupt;
                    // handing that piece to the order's owner would be theft.
                    await transaction.RollbackAsync();
                    Console.WriteLine($"MarketCancel refused: order {orderId} of player {playerId} escrows piece {escrowed.Id} owned by {escrowed.PlayerId}.");
                    return new MarketCancelOutcome(MarketCancelResult.NotYours);
                }

                // The order goes first: it references the escrowed row.
                db.MarketOrderRecords.Remove(order);
                await db.SaveChangesAsync();

                if (escrowed != null)
                {
                    db.MarketEquipmentInstances.Remove(escrowed);
                    var returned = new EquipmentInstance
                    {
                        PlayerId = playerId,
                        BaseItemId = escrowed.BaseItemId,
                        QualityTier = escrowed.QualityTier,
                        AffixPayload = escrowed.AffixPayload,
                        IsAffixLocked = escrowed.IsAffixLocked,
                    };
                    db.EquipmentInstances.Add(returned);
                    await db.SaveChangesAsync();
                    returnedEquipmentId = returned.Id;
                }
                else
                {
                    // Nothing in escrow: the listing could never have been
                    // bought (BuyItemAsync answers TargetNotFound), so taking
                    // the dead row down loses nothing and unsticks the screen.
                    Console.WriteLine($"MarketCancel: order {orderId} of player {playerId} had no escrowed piece; removed the dead order.");
                }
            }
            else
            {
                // A commodity order, or a shape nothing writes today. Its
                // escrow would be materials this method does not know how to
                // return, so refuse rather than delete the only record of them.
                await transaction.RollbackAsync();
                Console.WriteLine($"MarketCancel refused: order {orderId} is {order.OrderType} with no equipment escrow.");
                return new MarketCancelOutcome(MarketCancelResult.Unsupported);
            }

            await transaction.CommitAsync();
            Console.WriteLine($"MarketCancel: order {orderId} ({order.OrderType}) cancelled by {playerId}; piece {returnedEquipmentId?.ToString() ?? "-"}, gold {refundedGold}.");
            return new MarketCancelOutcome(MarketCancelResult.Ok, returnedEquipmentId, refundedGold);
        }

        /// <summary>
        /// The burned market fee's rate by the seller's gold: 5%, 8% from
        /// 500,000, 15% above 5,000,000. Shared by equipment and (task 54)
        /// cosmetic sales so the two markets cannot tax differently.
        /// </summary>
        internal static double WealthFeeRate(long sellerWealth)
        {
            if (sellerWealth > 5000000) return 0.15;
            if (sellerWealth >= 500000) return 0.08;
            return 0.05;
        }

        /// <summary>
        /// The seller's guild's sales tax on <paramref name="executionPrice"/>,
        /// deposited into that guild's gold ledger on the caller's transaction.
        /// Answers the amount taken; 0 when the seller has no guild.
        /// </summary>
        internal static async Task<long> ApplyGuildSalesTaxAsync(FolkIdleDbContext db, long sellerId, long executionPrice)
        {
            long guildTax = 0L;
            var sellerRecord = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", sellerId)
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

            return guildTax;
        }
    }
}
