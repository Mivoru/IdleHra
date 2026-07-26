using System;
using System.Data;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using FolkIdle.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Combat
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

                // Modul: Full-Stack Expansion, Part 1. Leggings check
                // FIRST - existing leggings base ids (e.g.
                // "transcendent_platelegs_leggings_armor_slot_base")
                // contain BOTH the "_leggings_" marker and the generic
                // "_armor_slot_" marker, so the more specific slot must
                // win before the armor fallback claims them for the chest
                // slot (their pre-leggings-slot behavior).
                bool isLeggings = item.BaseItemId.Contains("_leggings_");
                bool isWeapon = !isLeggings && item.BaseItemId.Contains("_weapon_slot_");
                bool isArmor = !isLeggings && item.BaseItemId.Contains("_armor_slot_");

                if (!isWeapon && !isArmor && !isLeggings)
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
                else if (isLeggings)
                {
                    player.EquippedLeggingsId = item.Id;
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

        public const int SlotWeapon = 0;
        public const int SlotArmor = 1;
        public const int SlotLeggings = 2;

        // Modul: Full-Stack Expansion, Part 1. Slot selector widened from
        // the old bool isArmorSlot to a 3-way index for the Leggings slot;
        // SimulationEngine's UnequipItem command maps the wire fields onto
        // it (see that handler's own comment).
        public async Task UnequipItemAsync(long playerId, int slotIndex)
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

                if (slotIndex == SlotArmor)
                {
                    player.EquippedArmorId = null;
                }
                else if (slotIndex == SlotLeggings)
                {
                    player.EquippedLeggingsId = null;
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
            (EquippedAffixTotals totals, int weaponSetId, int armorSetId, int leggingsSetId) = await ComputeEquippedTotalsAsync(db, player.EquippedWeaponId, player.EquippedArmorId, player.EquippedLeggingsId);

            return new EquipmentSlotUpdateNotification
            {
                PlayerId = player.Id,
                EquippedWeaponId = player.EquippedWeaponId ?? 0L,
                EquippedArmorId = player.EquippedArmorId ?? 0L,
                EquippedLeggingsId = player.EquippedLeggingsId ?? 0L,
                AffixTotals = totals,
                EquippedWeaponSetId = weaponSetId,
                EquippedArmorSetId = armorSetId,
                EquippedLeggingsSetId = leggingsSetId
            };
        }

        // Shared with StateCheckpointManager.LoadPlayerState, which needs the
        // same combined totals at login time (persisted EquippedWeaponId/ArmorId
        // are hydrated from PlayerRecords, but the derived stat totals are not
        // themselves persisted - they must be recomputed once here rather than
        // reading stale/zeroed values until the player's next equip action).
        public static async Task<(EquippedAffixTotals Totals, int WeaponSetId, int ArmorSetId, int LeggingsSetId)> ComputeEquippedTotalsAsync(FolkIdleDbContext db, long? weaponId, long? armorId, long? leggingsId = null)
        {
            EquippedAffixTotals totals = default;
            int weaponSetId = 0, armorSetId = 0, leggingsSetId = 0;

            if (weaponId.HasValue)
            {
                var weapon = await db.EquipmentInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == weaponId.Value);
                if (weapon != null)
                {
                    AddAffixTotals(weapon.AffixPayload, ref totals);
                    weaponSetId = weapon.SetId;
                }
            }

            if (armorId.HasValue)
            {
                var armor = await db.EquipmentInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == armorId.Value);
                if (armor != null)
                {
                    AddAffixTotals(armor.AffixPayload, ref totals);
                    armorSetId = armor.SetId;
                }
            }

            // Modul: Full-Stack Expansion, Part 1. Leggings contribute to
            // the same combined totals - the affix payload carries the
            // slot's defensive weighting from generation time, so no
            // slot-specific stat scaling belongs here.
            if (leggingsId.HasValue)
            {
                var leggings = await db.EquipmentInstances.AsNoTracking().FirstOrDefaultAsync(e => e.Id == leggingsId.Value);
                if (leggings != null)
                {
                    AddAffixTotals(leggings.AffixPayload, ref totals);
                    leggingsSetId = leggings.SetId;
                }
            }

            return (totals, weaponSetId, armorSetId, leggingsSetId);
        }

        // Modul: Affix System Unification. Reads GDD affix ids
        // (AffixRegistry, Module 14 section 1.3) and folds each into the stat
        // it actually belongs to.
        //
        // Before this it understood only the four numeric keys "1".."4", which
        // meant every GDD-named affix - i.e. everything AffixRerollEngine had
        // ever written - contributed exactly zero, and "5" (flat HP, written by
        // every drop) was read by nothing at all.
        //
        // The legacy numeric keys are still honoured so items already in
        // players' backpacks keep the stats they were generated with; there is
        // no migration and none is needed.
        //
        // "is_affix_locked" may also be present as a bool in the same object
        // (see ForgeSplicingEngine), so this parses defensively via JsonNode
        // rather than a typed Dictionary<string,int> that would throw on the
        // mixed-type payload.
        private static void AddAffixTotals(string affixPayload, ref EquippedAffixTotals totals)
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

                // Legacy numeric keys from items generated before the affix
                // ids were unified.
                switch (kvp.Key)
                {
                    case "1": totals.FlatAttack += magnitude; continue;
                    case "2": totals.FlatDefense += magnitude; continue;
                    case "3": totals.CritChanceTenthsPct += magnitude * 10; continue;
                    case "4": totals.LootLuckTenthsPct += magnitude * 10; continue;
                    case "5": totals.FlatHp += magnitude; continue;
                }

                switch (AffixRegistry.StripStackSuffix(kvp.Key))
                {
                    case "flat_hp": totals.FlatHp += magnitude; break;
                    case "flat_armor": totals.FlatDefense += magnitude; break;
                    case "armor_pen_flat": totals.FlatArmorPenetration += magnitude; break;

                    // The three damage-type percentages all raise the same
                    // effective attack in this combat model - there is one
                    // damage number, not per-type resistances - so they sum
                    // into one accumulator rather than pretending to be
                    // three independent stats.
                    case "melee_dmg_pct":
                    case "range_dmg_pct":
                    case "magic_dmg_pct": totals.DamageTenthsPct += magnitude; break;

                    case "attack_speed_pct": totals.AttackSpeedTenthsPct += magnitude; break;
                    case "crit_chance_pct": totals.CritChanceTenthsPct += magnitude; break;
                    case "crit_dmg_pct": totals.CritDamageTenthsPct += magnitude; break;
                    case "lifesteal_pct": totals.LifestealTenthsPct += magnitude; break;
                    case "dodge_chance_pct": totals.DodgeTenthsPct += magnitude; break;
                    case "block_chance_pct": totals.BlockTenthsPct += magnitude; break;
                }
            }
        }
    }
}
