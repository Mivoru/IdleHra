using System;
using System.Data;
using System.Globalization;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    public class ChronoCoreEngine
    {
        private const double ChronoCoreSeconds = 14400.0;
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public ChronoCoreEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task ConsumeChronoCoreAsync(long playerId, long chronoCoreItemId)
        {
            if (playerId <= 0 || chronoCoreItemId <= 0)
            {
                TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 24, Value2 = 4, Timestamp = Environment.TickCount64 });
                return;
            }

            string itemId = chronoCoreItemId.ToString(CultureInfo.InvariantCulture);

            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();

            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
            try
            {
                var core = await db.CommodityRecords
                    .FromSqlRaw("SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {0} AND \"ItemId\" = {1} FOR UPDATE", playerId, itemId)
                    .SingleOrDefaultAsync();

                if (core == null || core.Quantity <= 0)
                {
                    TelemetryStreamer.TryWrite(new TelemetryEvent { PlayerId = playerId, EventType = 3, Value1 = 24, Value2 = 5, Timestamp = Environment.TickCount64 });
                    await transaction.RollbackAsync();
                    return;
                }

                core.Quantity--;
                await db.SaveChangesAsync();
                await transaction.CommitAsync();

                _playerRegistry.ChronoAccelerationQueue.Enqueue(new ChronoAccelerationNotification
                {
                    PlayerId = playerId,
                    SecondsToAdd = ChronoCoreSeconds
                });
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Chrono core consumption failed: {ex.Message}");
            }
        }
    }
}
