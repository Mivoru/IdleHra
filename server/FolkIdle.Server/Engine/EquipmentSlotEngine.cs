using System;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FolkIdle.Server.Engine
{
    // Modul 16/21: equip/unequip for owned gear. Operates on EquipmentInstances -
    // the table CraftingEngine/loot/mail actually deposit owned gear into. Items
    // currently listed for sale or mid-Forge-fusion live in the separate
    // MarketEquipmentInstances table and are not equippable through this path;
    // reconciling those two tables is a larger, pre-existing fragmentation this
    // change does not attempt to fix.
    //
    // Weapon-vs-armor classification uses the same BaseItemId naming convention
    // ContentRegistry's item catalog already follows consistently (e.g.
    // "copper_greatsword_melee_weapon_slot_base", "iron_breastplate_chest_armor_slot_base")
    // rather than a new type column.
    public class EquipmentSlotEngine
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly PlayerSessionRegistry? _playerRegistry;

        public EquipmentSlotEngine(IServiceProvider serviceProvider, PlayerSessionRegistry? playerRegistry = null)
        {
            _serviceProvider = serviceProvider;
            _playerRegistry = playerRegistry;
        }

        public async Task EquipItemAsync(long playerId, long itemInstanceId)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                var item = await db.EquipmentInstances
                    .FromSqlInterpolated($"SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {itemInstanceId} FOR UPDATE")
                    .SingleOrDefaultAsync();

                if (item == null || item.PlayerId != playerId)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                bool isWeapon = item.BaseItemId.Contains("_weapon_slot_");
                bool isArmor = item.BaseItemId.Contains("_armor_slot_");

                if (!isWeapon && !isArmor)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var player = await db.PlayerRecords
                    .FromSqlRaw("SELECT * FROM \"PlayerRecords\" WHERE \"Id\" = {0} FOR UPDATE", playerId)
                    .SingleOrDefaultAsync();

                if (player == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Modul: Advanced Economy Refactoring, Part 2.3. Level
                // gate at equip time - the second half of the anti-cheese
                // lock (MarketEscrowEngine.BuyItemAsync blocks the
                // purchase; this blocks equipping over-leveled gear
                // acquired through any other channel: mail, bank
                // withdrawal, pre-gate inventory).
                int requiredLevel = EquipmentLevelGate.DeriveRequiredLevel(item.BaseItemId, item.QualityTier);
                if (player.CurrentLevel < requiredLevel)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Equip rejected: player {playerId} level {player.CurrentLevel} below required {requiredLevel} for {item.BaseItemId} T{item.QualityTier}.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.LevelTooLow);
                    return;
                }

                if (isWeapon)
                {
                    player.EquippedWeaponId = item.Id;
                }
                else
                {
                    player.EquippedArmorId = item.Id;
                }

                await db.SaveChangesAsync();

                EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, player);

                await transaction.CommitAsync();

                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Equip item failed for player {playerId}: {ex.Message}");
            }
        }

        public async Task UnequipItemAsync(long playerId, bool isArmorSlot)
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
                    return;
                }

                if (isArmorSlot)
                {
                    player.EquippedArmorId = null;
                }
                else
                {
                    player.EquippedWeaponId = null;
                }

                await db.SaveChangesAsync();

                EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, player);

                await transaction.CommitAsync();

                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Unequip item failed for player {playerId}: {ex.Message}");
            }
        }

        // Re-derives the combined equipped-gear stat totals from whichever of
        // the two slots are currently set, so both EquipItemAsync and
        // UnequipItemAsync always report a fully consistent snapshot regardless
        // of which single slot just changed.
        private static async Task<EquipmentSlotUpdateNotification> BuildNotificationAsync(FolkIdleDbContext db, PlayerRecord player)
        {
            (int attack, int defense, int crit, int luck) = await ComputeEquippedTotalsAsync(db, player.EquippedWeaponId, player.EquippedArmorId);

            return new EquipmentSlotUpdateNotification
            {
                PlayerId = player.Id,
                EquippedWeaponId = player.EquippedWeaponId ?? 0L,
                EquippedArmorId = player.EquippedArmorId ?? 0L,
                EquippedFlatAttack = attack,
                EquippedFlatDefense = defense,
                EquippedCritBonus = crit,
                EquippedLuckBonus = luck
            };
        }

        // Shared with StateCheckpointManager.LoadPlayerState, which needs the
        // same combined totals at login time (persisted EquippedWeaponId/ArmorId
        // are hydrated from PlayerRecords, but the derived stat totals are not
        // themselves persisted - they must be recomputed once here rather than
        // reading stale/zeroed values until the player's next equip action).
        public static async Task<(int Attack, int Defense, int Crit, int Luck)> ComputeEquippedTotalsAsync(FolkIdleDbContext db, long? weaponId, long? armorId)
        {
            int totalAttack = 0, totalDefense = 0, totalCrit = 0, totalLuck = 0;

            if (weaponId.HasValue)
            {
                var weapon = await db.EquipmentInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == weaponId.Value);
                if (weapon != null)
                {
                    AddAffixTotals(weapon.AffixPayload, ref totalAttack, ref totalDefense, ref totalCrit, ref totalLuck);
                }
            }

            if (armorId.HasValue)
            {
                var armor = await db.EquipmentInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == armorId.Value);
                if (armor != null)
                {
                    AddAffixTotals(armor.AffixPayload, ref totalAttack, ref totalDefense, ref totalCrit, ref totalLuck);
                }
            }

            return (totalAttack, totalDefense, totalCrit, totalLuck);
        }

        // Affix keys are the plain numeric slot ids EquipmentGenerator writes
        // ("1"=attack, "2"=defense, "3"=crit, "4"=luck); "is_affix_locked" may
        // also be present as a bool in the same object (see ForgeSplicingEngine),
        // so this parses defensively via JsonNode rather than a typed
        // Dictionary<string,int> that would throw on the mixed-type payload.
        private static void AddAffixTotals(string affixPayload, ref int attack, ref int defense, ref int crit, ref int luck)
        {
            if (string.IsNullOrWhiteSpace(affixPayload) || JsonNode.Parse(affixPayload) is not JsonObject affixObject)
            {
                return;
            }

            foreach (var kvp in affixObject)
            {
                if (kvp.Key == "is_affix_locked" || kvp.Value is not JsonValue affixValue)
                {
                    continue;
                }

                if (!affixValue.TryGetValue(out int magnitude))
                {
                    continue;
                }

                switch (kvp.Key)
                {
                    case "1": attack += magnitude; break;
                    case "2": defense += magnitude; break;
                    case "3": crit += magnitude; break;
                    case "4": luck += magnitude; break;
                }
            }
        }
    }
}
