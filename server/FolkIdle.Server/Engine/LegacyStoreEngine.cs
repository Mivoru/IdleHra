using System;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class LegacyStoreEngine
    {
        public const uint CitizenMultiSlotUnlockId = 1U;
        public const uint MaxCitizenSlotIndex = 31U;

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
                if (playerId <= 0 || targetUnlockId != CitizenMultiSlotUnlockId || requestedSlotIndex > MaxCitizenSlotIndex)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 25, Value2 = 4, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
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

                int requestedMask = 1 << (int)requestedSlotIndex;
                if ((unlockedMask & requestedMask) != 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 25, Value2 = 5, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                int cost = CalculateCitizenSlotCost(requestedSlotIndex);
                if (totalBalance < cost)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 25, Value2 = cost, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
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
                await transaction.RollbackAsync();
                Console.WriteLine($"Legacy unlock purchase failed: {ex.Message}");
            }
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

            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
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
