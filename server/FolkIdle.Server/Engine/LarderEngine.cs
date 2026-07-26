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

        // slotIndex is 0-based on the wire and 1-based in the payload field
        // names; this is the only place the two conventions meet.
        //
        // A quantity of 0 unloads the slot, returning whatever is stocked to
        // the backpack. Anything else stocks that many units of foodItemId,
        // consuming them from the backpack. Stocking a different food into an
        // occupied slot returns the old contents first, so a slot can never
        // silently destroy what was in it.
        public async Task ExecuteStockFoodSlotAsync(long playerId, int slotIndex, int foodItemId, int quantity)
        {
            if (slotIndex < 0 || slotIndex >= Network.LarderLimits.SlotCount)
            {
                _playerRegistry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            bool isUnload = quantity <= 0;
            if (!isUnload && !FoodRegistry.IsFood(foodItemId))
            {
                _playerRegistry.EnqueueCommandResult(playerId, (byte)Network.CommandResultCode.GenericValidationFailure);
                return;
            }

            var retryingOptions = _serviceProvider.GetRequiredService<RetryingDbContextOptions>();
            await using var context = new FolkIdleDbContext(retryingOptions.Options);
            var strategy = context.Database.CreateExecutionStrategy();

            // Same result-tuple-not-exception shape as CraftingEngine: running
            // out of food in the backpack is an ordinary outcome to report, not
            // a fault to retry.
            (Network.CommandResultCode result, int storedItemId, int storedCount) = await strategy.ExecuteAsync(async () =>
            {
                context.ChangeTracker.Clear();
                using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                var player = await context.PlayerRecords
                    .FromSqlInterpolated($"SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {playerId} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.RollbackAsync();
                    return (Network.CommandResultCode.TargetNotFound, 0, 0);
                }

                (int existingItemId, int existingCount) = ReadSlot(player, slotIndex);

                // Return the current contents to the backpack whenever the slot
                // is being emptied or repurposed. Deposited rather than
                // discarded - the player paid materials and cooking time for
                // every one of these.
                bool replacingContents = existingCount > 0 && (isUnload || existingItemId != foodItemId);
                if (replacingContents)
                {
                    string existingBaseId = ContentRegistry.GetItemBaseId(existingItemId);
                    if (!string.IsNullOrEmpty(existingBaseId))
                    {
                        await ReturnToBackpackAsync(context, playerId, existingBaseId, existingCount);
                    }

                    existingItemId = 0;
                    existingCount = 0;
                }

                int newCount = existingCount;
                int newItemId = isUnload ? 0 : foodItemId;

                if (!isUnload)
                {
                    int headroom = Network.LarderLimits.SlotCapacity - existingCount;
                    if (headroom <= 0)
                    {
                        await transaction.RollbackAsync();
                        return (Network.CommandResultCode.InventoryFull, existingItemId, existingCount);
                    }

                    int toMove = Math.Min(quantity, headroom);

                    string foodBaseId = ContentRegistry.GetItemBaseId(foodItemId);
                    if (string.IsNullOrEmpty(foodBaseId))
                    {
                        await transaction.RollbackAsync();
                        return (Network.CommandResultCode.GenericValidationFailure, existingItemId, existingCount);
                    }

                    if (!await InventoryAndStashSystem.TryConsumeUnifiedAsync(context, playerId, foodBaseId, toMove))
                    {
                        await transaction.RollbackAsync();
                        return (Network.CommandResultCode.InsufficientMaterials, existingItemId, existingCount);
                    }

                    newCount = existingCount + toMove;
                }
                else
                {
                    newCount = 0;
                }

                WriteSlot(player, slotIndex, newItemId, newCount);

                await context.SaveChangesAsync();
                await transaction.CommitAsync();
                return (Network.CommandResultCode.Success, newItemId, newCount);
            });

            _playerRegistry.EnqueueCommandResult(playerId, (byte)result);

            if (result != Network.CommandResultCode.Success)
            {
                return;
            }

            // The live TickStatePayload is owned exclusively by the 10Hz tick
            // thread and must never be written from here - this is the same
            // hand-off ActivityChangeQueue uses.
            _playerRegistry.LarderSlotUpdateQueue.Enqueue(new LarderSlotUpdateNotification
            {
                PlayerId = playerId,
                SlotIndex = slotIndex,
                ItemId = storedItemId,
                Count = storedCount
            });
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

            var existing = await context.CommodityRecords
                .FromSqlInterpolated($"SELECT * FROM \"CommodityRecords\" WHERE \"PlayerId\" = {playerId} AND \"ItemId\" = {baseItemId} FOR UPDATE")
                .SingleOrDefaultAsync();

            if (existing == null)
            {
                context.CommodityRecords.Add(new CommodityRecord { PlayerId = playerId, ItemId = baseItemId, Quantity = quantity });
            }
            else
            {
                existing.Quantity += quantity;
            }
        }

        private static (int ItemId, int Count) ReadSlot(PlayerRecord player, int slotIndex) => slotIndex switch
        {
            0 => (player.LarderSlot1ItemId, player.LarderSlot1Count),
            1 => (player.LarderSlot2ItemId, player.LarderSlot2Count),
            _ => (player.LarderSlot3ItemId, player.LarderSlot3Count)
        };

        private static void WriteSlot(PlayerRecord player, int slotIndex, int itemId, int count)
        {
            switch (slotIndex)
            {
                case 0:
                    player.LarderSlot1ItemId = itemId;
                    player.LarderSlot1Count = count;
                    break;
                case 1:
                    player.LarderSlot2ItemId = itemId;
                    player.LarderSlot2Count = count;
                    break;
                default:
                    player.LarderSlot3ItemId = itemId;
                    player.LarderSlot3Count = count;
                    break;
            }
        }
    }
}
