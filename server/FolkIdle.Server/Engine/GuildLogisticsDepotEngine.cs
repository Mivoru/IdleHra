using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class GuildLogisticsDepotEngine
    {
        private const long DefaultTargetRequirement = 10000L;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public GuildLogisticsDepotEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task DepositMaterialAsync(long playerId, long guildId, uint materialId, uint depositQuantity)
        {
            if (playerId <= 0 || guildId <= 0 || materialId == 0 || depositQuantity == 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 26, Value2 = 1, Timestamp = Environment.TickCount64 });
                return;
            }

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var guild = await db.GuildRecords
                    .FromSqlRaw("SELECT * FROM \"GuildRecords\" WHERE \"Id\" = {0} FOR UPDATE", guildId)
                    .SingleOrDefaultAsync();

                if (guild == null)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 26, Value2 = 2, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (player == null || player.GuildId != guildId)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 26, Value2 = 3, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                int materialKey = (int)materialId;
                long quantity = depositQuantity;
                string itemId = materialKey.ToString(CultureInfo.InvariantCulture);

                var commodity = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                    .SingleOrDefaultAsync();

                if (commodity == null || commodity.Quantity < quantity)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 4, Value1 = 26, Value2 = materialKey, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                var depot = await db.GuildLogisticsDepots
                    .FromSqlRaw("SELECT * FROM \"GuildLogisticsDepots\" WHERE \"GuildId\" = {0} AND \"MaterialId\" = {1} FOR UPDATE", guildId, materialKey)
                    .SingleOrDefaultAsync();

                if (depot == null)
                {
                    depot = new GuildLogisticsDepot
                    {
                        GuildId = guildId,
                        MaterialId = materialKey,
                        CurrentStock = 0L,
                        TargetRequirement = DefaultTargetRequirement
                    };
                    db.GuildLogisticsDepots.Add(depot);
                }

                var ledger = await db.GuildContributionLedgers
                    .FromSqlRaw("SELECT * FROM \"GuildContributionLedgers\" WHERE \"PlayerId\" = {0} AND \"GuildId\" = {1} AND \"MaterialId\" = {2} FOR UPDATE", playerId, guildId, materialKey)
                    .SingleOrDefaultAsync();

                if (ledger == null)
                {
                    ledger = new GuildContributionLedger
                    {
                        PlayerId = playerId,
                        GuildId = guildId,
                        MaterialId = materialKey,
                        LifetimeContributed = 0L
                    };
                    db.GuildContributionLedgers.Add(ledger);
                }

                commodity.Quantity -= quantity;
                depot.CurrentStock += quantity;
                ledger.LifetimeContributed += quantity;

                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.GuildLogisticsDepotUpdateQueue.Enqueue(new GuildLogisticsDepotUpdateNotification
                {
                    GuildId = guildId,
                    MaterialId = materialKey,
                    CurrentStock = depot.CurrentStock,
                    TargetRequirement = depot.TargetRequirement
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Guild logistics deposit failed: {ex.Message}");
            }
        }
    }
}
