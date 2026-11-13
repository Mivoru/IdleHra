using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    public class LegacyStoreEngine
    {
        public const uint CitizenMultiSlotUnlockId = 1U;
        public const uint MaxCitizenSlotIndex = 31U;

        // Modul: Prestige perk-tree unlock ids, dispatched from the same
        // CommandType.PurchaseLegacyUnlocks wire command as the citizen-
        // slot unlock above (requestedSlotIndex is unused/ignored for
        // these three - ClientCommandPacket has no separate "perk" command
        // and adding one would duplicate this entire validation/locking
        // path for no behavioral difference).
        public const uint XpMultiplierPerkUnlockId = 2U;
        public const uint GoldDropRatePerkUnlockId = 3U;
        public const uint CombatSpeedPerkUnlockId = 4U;

        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public LegacyStoreEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task PurchaseLegacyUnlockAsync(long playerId, uint targetUnlockId, uint requestedSlotIndex)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                bool isPerkUnlock = targetUnlockId == XpMultiplierPerkUnlockId || targetUnlockId == GoldDropRatePerkUnlockId || targetUnlockId == CombatSpeedPerkUnlockId;
                bool isValidTarget = isPerkUnlock || targetUnlockId == CitizenMultiSlotUnlockId;

                if (playerId <= 0 || !isValidTarget || (!isPerkUnlock && requestedSlotIndex > MaxCitizenSlotIndex))
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 25, Value2 = 4, Timestamp = Environment.TickCount64 });
                    await transaction.SafeRollbackAsync();
                    return;
                }

                int activeEraId = await ResolveActiveEraIdAsync(db);
                var ledgers = await db.PlayerLegacyLedgers
                    .FromSqlRaw("SELECT * FROM \"PlayerLegacyLedgers\" WHERE \"PlayerId\" = {0} ORDER BY \"EraId\" FOR UPDATE", playerId)
                    .ToListAsync();

                if (ledgers.Count == 0)
                {
                    var created = new PlayerLegacyLedger
                    {
                        PlayerId = playerId,
                        EraId = activeEraId,
                        LegacyShardBalance = 0,
                        CitizenMultiSlotsUnlocked = 0
                    };
                    db.PlayerLegacyLedgers.Add(created);
                    ledgers.Add(created);
                }

                int unlockedMask = 0;
                long totalBalance = 0L;
                for (int i = 0; i < ledgers.Count; i++)
                {
                    unlockedMask |= ledgers[i].CitizenMultiSlotsUnlocked;
                    totalBalance += ledgers[i].LegacyShardBalance;
                }

                if (isPerkUnlock)
                {
                    await ExecutePerkPurchaseAsync(db, transaction, playerId, targetUnlockId, ledgers, totalBalance, (int)Math.Min(requestedSlotIndex, (uint)MaxRanksPerPurchase));
                    return;
                }

                int requestedMask = 1 << (int)requestedSlotIndex;
                if ((unlockedMask & requestedMask) != 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 25, Value2 = 5, Timestamp = Environment.TickCount64 });
                    await transaction.SafeRollbackAsync();
                    return;
                }

                int cost = CalculateCitizenSlotCost(requestedSlotIndex);
                if (totalBalance < cost)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 25, Value2 = cost, Timestamp = Environment.TickCount64 });
                    await transaction.SafeRollbackAsync();
                    return;
                }

                int remainingCost = cost;
                foreach (var ledger in ledgers.OrderByDescending(l => l.EraId))
                {
                    if (remainingCost <= 0) break;
                    int debit = Math.Min(ledger.LegacyShardBalance, remainingCost);
                    ledger.LegacyShardBalance -= debit;
                    remainingCost -= debit;
                }

                var targetLedger = ledgers.OrderByDescending(l => l.EraId).First();
                targetLedger.CitizenMultiSlotsUnlocked = unlockedMask | requestedMask;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                int newBalance = (int)Math.Min(int.MaxValue, totalBalance - cost);
                _playerRegistry.LegacyStoreUpdateQueue.Enqueue(new LegacyStoreUpdateNotification
                {
                    PlayerId = playerId,
                    LegacyShardBalance = newBalance,
                    CitizenMultiSlotsUnlocked = unlockedMask | requestedMask
                });
            }
            catch (Exception ex)
            {
                await transaction.SafeRollbackAsync();
                Console.WriteLine($"Legacy unlock purchase failed: {ex.Message}");
                // Modul: a catch that answered nothing left the button waiting for its timeout; a lost race now says "try again" (TransientDbConflict.RefusalFor).
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Domain.Shared.TransientDbConflict.RefusalFor(ex));
            }
        }

        // Modul: Prestige perk-tree purchase - shares the citizen-slot
        // path's ledger loading/locking and debit-oldest-era-first spend
        // order (see the caller), but the purchased effect lives on
        // PlayerRecord.LegacyPerks, not on any single era's ledger row,
        // since perk power is meant to persist across every future era
        // rather than resetting with CitizenMultiSlotsUnlocked.
        /// <summary>The most ranks one Boon purchase may buy (the largest Boon has 25).</summary>
        public const int MaxRanksPerPurchase = 25;

        /// <summary>
        /// How many ranks a balance buys from <paramref name="currentRank"/>, up to
        /// <paramref name="wanted"/> and the Boon's cap, and what they cost.
        /// </summary>
        public static (int Ranks, long Cost) RanksAffordable(int bitOffset, int currentRank, long balance, int wanted)
        {
            int max = LegacyPerkResolver.MaxRankFor(bitOffset);
            int ranks = 0;
            long cost = 0;
            while (ranks < wanted && currentRank + ranks < max)
            {
                long next = LegacyPerkResolver.CalculatePerkRankCost(bitOffset, currentRank + ranks);
                if (cost + next > balance) break;
                cost += next;
                ranks++;
            }
            return (ranks, cost);
        }

        // Modul: SEVERAL RANKS IN ONE COMMAND (2026-11-13). The endgame accounts
        // held 40k-55k shards, about sixty ranks' worth, and each rank was one
        // tap - sixty fast taps, the exact rhythm the macro detector exists to
        // catch. RequestedSlotIndex, which only the citizen-slot half reads,
        // carries how many ranks to buy (0 and 1 both mean one, as every older
        // client sends); the server buys as many as the balance covers in one
        // transaction.
        private async Task ExecutePerkPurchaseAsync(FolkIdleDbContext db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, long playerId, uint targetUnlockId, System.Collections.Generic.List<PlayerLegacyLedger> ledgers, long totalBalance, int ranksWanted)
        {
            int bitOffset = targetUnlockId switch
            {
                XpMultiplierPerkUnlockId => LegacyPerkResolver.XpMultiplierBitOffset,
                GoldDropRatePerkUnlockId => LegacyPerkResolver.GoldDropRateBitOffset,
                CombatSpeedPerkUnlockId => LegacyPerkResolver.CombatSpeedBitOffset,
                _ => -1
            };

            if (bitOffset < 0)
            {
                await transaction.SafeRollbackAsync();
                return;
            }

            var player = await db.PlayerRecords
                .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                .SingleOrDefaultAsync();

            if (player == null)
            {
                await transaction.SafeRollbackAsync();
                return;
            }

            int currentRank = LegacyPerkResolver.GetPerkRank(player.LegacyPerks, bitOffset);
            if (currentRank >= LegacyPerkResolver.MaxRankFor(bitOffset))
            {
                await transaction.SafeRollbackAsync();
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.BoonMaxed);
                return;
            }

            var (ranksBought, totalCost) = RanksAffordable(bitOffset, currentRank, totalBalance, Math.Max(1, ranksWanted));
            int cost = (int)Math.Min(int.MaxValue, totalCost);

            if (ranksBought == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 25, Value2 = LegacyPerkResolver.CalculatePerkRankCost(bitOffset, currentRank), Timestamp = Environment.TickCount64 });
                await transaction.SafeRollbackAsync();
                // Modul: say so (2026-11-13) - this refusal used to answer nothing.
                _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.BoonInsufficientShards);
                return;
            }

            int remainingCost = cost;
            foreach (var ledger in ledgers.OrderByDescending(l => l.EraId))
            {
                if (remainingCost <= 0) break;
                int debit = Math.Min(ledger.LegacyShardBalance, remainingCost);
                ledger.LegacyShardBalance -= debit;
                remainingCost -= debit;
            }

            player.LegacyPerks = LegacyPerkResolver.SetPerkRank(player.LegacyPerks, bitOffset, currentRank + ranksBought);

            await db.SaveChangesAsync();
            await transaction.CommitAsync();

            int newBalance = (int)Math.Min(int.MaxValue, totalBalance - cost);
            _playerRegistry.LegacyStoreUpdateQueue.Enqueue(new LegacyStoreUpdateNotification
            {
                PlayerId = playerId,
                LegacyShardBalance = newBalance,
                LegacyPerks = player.LegacyPerks,
                HasLegacyPerksUpdate = true
            });
            _playerRegistry.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.BoonPurchased);
        }

        public static int CalculateCitizenSlotCost(uint requestedSlotIndex)
        {
            if (requestedSlotIndex > MaxCitizenSlotIndex)
            {
                return int.MaxValue;
            }
            return 25 + ((int)requestedSlotIndex * 10);
        }

        private static async Task<int> ResolveActiveEraIdAsync(FolkIdleDbContext db)
        {
            var activeEra = await db.SeasonalEraRecords
                .Where(e => e.IsActive)
                .OrderByDescending(e => e.EraId)
                .FirstOrDefaultAsync();

            if (activeEra != null)
            {
                return activeEra.EraId;
            }

            long now = GameClock.UtcNowOffset.ToUnixTimeSeconds();
            var created = new SeasonalEraRecord
            {
                EndTimestamp = now + (90L * 24L * 60L * 60L),
                IsActive = true
            };
            db.SeasonalEraRecords.Add(created);
            await db.SaveChangesAsync();
            return created.EraId;
        }
    }
}
