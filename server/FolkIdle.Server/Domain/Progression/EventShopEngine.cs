using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Models;
using FolkIdle.Server.Network;
using Microsoft.EntityFrameworkCore;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>Currency handed back to the live payload after a purchase did not land.</summary>
    public readonly record struct EventShopRefund(long PlayerId, int EventId, int Amount);

    /// <summary>
    /// The database half of a seasonal event purchase. The tick has already
    /// taken the price off the live payload (it owns the balance); this saves
    /// what was bought, or hands the price back through <see cref="Refunds"/>.
    ///
    /// Modul: SPEND FIRST, REFUND ON FAILURE. The other order - save, then
    /// deduct - would need the database to know the balance, and the balance
    /// lives on the payload between checkpoints. Taking it on the tick means a
    /// double tap is refused by the balance itself, and every path that does
    /// not end in a saved row ends in a refund.
    /// </summary>
    public static class EventShopEngine
    {
        public static readonly ConcurrentQueue<EventShopRefund> Refunds = new();

        public static async Task BuyAsync(
            IDbContextFactory<FolkIdleDbContext> factory, PlayerSessionRegistry registry,
            long playerId, int eventId, EventShopItem item)
        {
            CommandResultCode result;
            try
            {
                await using var db = await factory.CreateDbContextAsync();
                result = await BuyCoreAsync(db, playerId, item, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Event shop purchase failed for player {playerId} ({item.Id}): {ex.Message}");
                result = CommandResultCode.CheckpointFailed;
            }

            if (result != CommandResultCode.EventShopBought)
            {
                Refunds.Enqueue(new EventShopRefund(playerId, eventId, item.Price));
            }
            registry.EnqueueCommandResult(playerId, (byte)result);
        }

        /// <summary>
        /// Saves the item unless it is already owned. The PlayerRecords row
        /// lock serialises two purchases of one account, so a double tap that
        /// the balance did not stop (enough for both) still saves one row.
        /// </summary>
        internal static async Task<CommandResultCode> BuyCoreAsync(FolkIdleDbContext db, long playerId, EventShopItem item, DateTime utcNow)
        {
            await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId);

            switch (item.Kind)
            {
                case EventShopKind.Avatar:
                {
                    bool owned = await db.CosmeticItems.AnyAsync(c => c.PlayerId == playerId && c.DefinitionId == item.Id);
                    if (owned)
                    {
                        await tx.RollbackAsync();
                        return CommandResultCode.EventShopAlreadyOwned;
                    }
                    var def = CosmeticRegistry.Find(item.Id);
                    db.CosmeticItems.Add(new CosmeticItem
                    {
                        PlayerId = playerId,
                        Kind = (byte)CosmeticKind.Avatar,
                        DefinitionId = item.Id,
                        Rarity = (byte)(def?.Rarity ?? CosmeticRegistry.Epic),
                        Source = (byte)CosmeticSource.Event,
                        AcquiredAtUtc = DateTime.SpecifyKind(utcNow, DateTimeKind.Utc),
                        IsListed = false,
                    });
                    break;
                }
                default:
                    await tx.RollbackAsync();
                    return CommandResultCode.GenericValidationFailure;
            }

            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return CommandResultCode.EventShopBought;
        }

        /// <summary>The shop item ids this player owns, for the shop screen.</summary>
        public static async Task<HashSet<string>> OwnedAsync(FolkIdleDbContext db, long playerId, IEnumerable<string> ids)
        {
            var wanted = ids.ToList();
            var owned = await db.CosmeticItems
                .Where(c => c.PlayerId == playerId && wanted.Contains(c.DefinitionId))
                .Select(c => c.DefinitionId)
                .ToListAsync();
            return new HashSet<string>(owned, StringComparer.Ordinal);
        }
    }
}
