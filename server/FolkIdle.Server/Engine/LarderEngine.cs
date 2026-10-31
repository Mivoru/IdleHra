using System;
using System.Data;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Models;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Engine
{
    // Modul: larder. Moves food out of the backpack and into one of the three
    // auto-eat slots, and back out again.
    //
    // This engine is the missing write side of the auto-eat system. Four
    // separate systems read TickStatePayload.Food{1,2,3}_ItemId/_Count - the
    // combat auto-eat step, both World Boss depletion checks, and the Chrono
    // warp catch-up - and nothing anywhere assigned them. There was no command,
    // no UI and no persistence, so every player's larder was empty forever and
    // any combat activity stopped the first time HP crossed the auto-eat
    // threshold, with nothing on screen explaining why. See
    // Network.ActivityHaltReason.OutOfFood for the other half of that fix.
    public class LarderEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry _playerRegistry;

        public LarderEngine(IServiceProvider serviceProvider, PlayerSessionRegistry playerRegistry)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        /// <summary>
        /// Takes <paramref name="quantity"/> of a food from the chest (backpack
        /// or stash) for a larder slot, in one serializable transaction. False
        /// when the chest does not hold that many. The slot itself belongs to
        /// the tick - see LarderTickCoordinator.HandleStockFoodSlot.
        /// </summary>
        public async Task<bool> TakeFoodFromChestAsync(long playerId, int foodItemId, int quantity)
        {
            string foodBaseId = ContentRegistry.GetItemBaseId(foodItemId);
            if (quantity <= 0 || string.IsNullOrEmpty(foodBaseId)) return false;

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var context = new FolkIdleDbContext(retryingOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            // Same result-not-exception shape as CraftingEngine: running out of
            // food in the chest is an ordinary outcome to report, not a fault.
            return await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, foodBaseId, quantity))
                {
                    await transaction.RollbackAsync();
                    return false;
                }
                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return true;
            });
        }

        /// <summary>
        /// Puts food that left a larder slot back in the chest - deposited
        /// rather than discarded, the player fished for every one of these.
        /// </summary>
        public async Task ReturnFoodToChestAsync(long playerId, int foodItemId, int quantity)
        {
            string baseId = ContentRegistry.GetItemBaseId(foodItemId);
            if (quantity <= 0 || string.IsNullOrEmpty(baseId)) return;

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var context = new FolkIdleDbContext(retryingOptions.Options);
            await ReturnToBackpackAsync(context, playerId, baseId, quantity);
            await context.SaveChangesAsync();
        }

        // Modul: larder. Persists the auto-eat threshold, which
        // CommandType.UpdateAutoEatThreshold previously wrote only to the live
        // payload - so the player's setting was discarded at every logout.
        public async Task PersistAutoEatThresholdAsync(long playerId, int thresholdPct)
        {
            if (thresholdPct < 0) thresholdPct = 0;
            if (thresholdPct > 100) thresholdPct = 100;

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var context = new FolkIdleDbContext(retryingOptions.Options);

            var player = await context.PlayerRecords.FirstOrDefaultAsync(p => p.Id == playerId);
            if (player == null) return;

            player.AutoEatThresholdPct = thresholdPct;
            await context.SaveChangesAsync();
        }

        // Unloaded food goes back where the player took it from - the backpack -
        // rather than through InventoryAndStashSystem.DepositToStashAsync, which
        // targets the stash tier and returns an overflow remainder the caller
        // would then have to place anyway.
        private static async Task ReturnToBackpackAsync(FolkIdleDbContext context, long playerId, string baseItemId, int quantity)
        {
            if (quantity <= 0) return;

            // Modul: an upsert (CommodityLedger), task 44. If this same
            // request already took from the stack (a swap), the ledger rebases
            // that tracked row so SaveChanges keeps both moves.
            await CommodityLedger.AddAsync(context, playerId, baseItemId, quantity);
        }
    }
}
