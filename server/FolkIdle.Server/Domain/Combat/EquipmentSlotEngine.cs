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

        // Modul: per-character equipment. The six slots a character can fill.
        // Widened from three (Weapon / Armor / Leggings): the old "Armor" slot
        // swallowed helmets, chest pieces, gloves and boots into one, so three
        // quarters of the armour catalogue could never be worn even though
        // AffixRegistry has always rolled slot-correct affixes for all of them.
        public const int SlotWeapon = 0;
        public const int SlotHelmet = 1;
        public const int SlotChest = 2;
        public const int SlotGloves = 3;
        public const int SlotLeggings = 4;
        public const int SlotBoots = 5;
        public const int SlotCount = 6;

        // Which slot a BaseItemId belongs in, or -1 if it is not equippable.
        //
        // Order matters. Every armour BaseId carries the generic "_armor_slot_"
        // marker as well as its specific one (e.g.
        // "transcendent_platelegs_leggings_armor_slot_base"), so the specific
        // markers must all be tested before any generic fallback - the old code
        // had exactly this bug in miniature, needing a comment to explain why
        // leggings had to be checked before armour.
        //
        // Unrecognised armour falls back to the chest slot, preserving the
        // pre-widening behaviour for any BaseId that carries only the generic
        // marker rather than silently becoming unequippable.
        public static int ResolveSlotIndex(string baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return -1;

            if (baseItemId.Contains("_helmet_")) return SlotHelmet;
            if (baseItemId.Contains("_gloves_")) return SlotGloves;
            if (baseItemId.Contains("_boots_")) return SlotBoots;
            if (baseItemId.Contains("_leggings_")) return SlotLeggings;
            if (baseItemId.Contains("_chest_")) return SlotChest;
            if (baseItemId.Contains("_weapon_slot_")) return SlotWeapon;
            if (baseItemId.Contains("_armor_slot_")) return SlotChest;

            return -1;
        }

        private static long? ReadSlot(CharacterRecord character, int slotIndex) => slotIndex switch
        {
            SlotWeapon => character.EquippedWeaponId,
            SlotHelmet => character.EquippedHelmetId,
            SlotChest => character.EquippedChestId,
            SlotGloves => character.EquippedGlovesId,
            SlotLeggings => character.EquippedLeggingsId,
            SlotBoots => character.EquippedBootsId,
            _ => null
        };

        private static void WriteSlot(CharacterRecord character, int slotIndex, long? itemInstanceId)
        {
            switch (slotIndex)
            {
                case SlotWeapon: character.EquippedWeaponId = itemInstanceId; break;
                case SlotHelmet: character.EquippedHelmetId = itemInstanceId; break;
                case SlotChest: character.EquippedChestId = itemInstanceId; break;
                case SlotGloves: character.EquippedGlovesId = itemInstanceId; break;
                case SlotLeggings: character.EquippedLeggingsId = itemInstanceId; break;
                case SlotBoots: character.EquippedBootsId = itemInstanceId; break;
            }
        }

        // Modul: per-character equipment. Account-wide worn check.
        //
        // Equipment moved from PlayerRecord to CharacterRecord, so "is this item
        // equipped?" stopped being a three-field comparison on one row and
        // became a question about every character the player owns. Anything that
        // would destroy, transfer or re-point an EquipmentInstances row - listing
        // it on the market, feeding it to a forge fusion, mailing it, wiping it
        // at season rollover - has to ask this, or it would leave a dangling
        // equip pointer on some OTHER character that the caller never looked at.
        //
        // Single query over the player's characters; not a hot path (every
        // caller is already inside a DB transaction), so the LINQ is fine here.
        public static async Task<bool> IsEquippedAnywhereAsync(FolkIdleDbContext db, long playerId, long itemInstanceId)
        {
            if (itemInstanceId <= 0) return false;

            return await db.CharacterRecords
                .AsNoTracking()
                .AnyAsync(c => c.PlayerId == playerId && (
                    c.EquippedWeaponId == itemInstanceId ||
                    c.EquippedHelmetId == itemInstanceId ||
                    c.EquippedChestId == itemInstanceId ||
                    c.EquippedGlovesId == itemInstanceId ||
                    c.EquippedLeggingsId == itemInstanceId ||
                    c.EquippedBootsId == itemInstanceId));
        }

        // Three-item variant for ForgeSplicingEngine, which locks a target and
        // two sacrifices together and would otherwise need three round trips
        // inside one Serializable transaction.
        public static async Task<bool> IsAnyEquippedAnywhereAsync(FolkIdleDbContext db, long playerId, long firstItemId, long secondItemId, long thirdItemId)
        {
            return await db.CharacterRecords
                .AsNoTracking()
                .AnyAsync(c => c.PlayerId == playerId && (
                    (c.EquippedWeaponId != null && (c.EquippedWeaponId == firstItemId || c.EquippedWeaponId == secondItemId || c.EquippedWeaponId == thirdItemId)) ||
                    (c.EquippedHelmetId != null && (c.EquippedHelmetId == firstItemId || c.EquippedHelmetId == secondItemId || c.EquippedHelmetId == thirdItemId)) ||
                    (c.EquippedChestId != null && (c.EquippedChestId == firstItemId || c.EquippedChestId == secondItemId || c.EquippedChestId == thirdItemId)) ||
                    (c.EquippedGlovesId != null && (c.EquippedGlovesId == firstItemId || c.EquippedGlovesId == secondItemId || c.EquippedGlovesId == thirdItemId)) ||
                    (c.EquippedLeggingsId != null && (c.EquippedLeggingsId == firstItemId || c.EquippedLeggingsId == secondItemId || c.EquippedLeggingsId == thirdItemId)) ||
                    (c.EquippedBootsId != null && (c.EquippedBootsId == firstItemId || c.EquippedBootsId == secondItemId || c.EquippedBootsId == thirdItemId))));
        }

        // Modul: per-character equipment. characterId names WHICH of the
        // player's characters is putting the item on. Guid.Empty means "the
        // main character", so a client that has not been taught about the
        // roster yet keeps working exactly as before.
        public async Task EquipItemAsync(long playerId, long itemInstanceId, Guid characterId = default)
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

                int slotIndex = ResolveSlotIndex(item.BaseItemId);
                if (slotIndex < 0)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var character = await ResolveCharacterForUpdateAsync(db, playerId, characterId);
                if (character == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                // Modul: per-character equipment. One physical item cannot be
                // worn by two characters at once. Without this, equipping the
                // same sword on all three would triple its stats across the
                // roster from a single drop.
                if (await IsEquippedAnywhereAsync(db, playerId, itemInstanceId))
                {
                    await transaction.RollbackAsync();
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped);
                    return;
                }

                int playerLevel = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.CurrentLevel)
                    .FirstOrDefaultAsync();

                // Modul: Advanced Economy Refactoring, Part 2.3. Level
                // gate at equip time - the second half of the anti-cheese
                // lock (MarketEscrowEngine.BuyItemAsync blocks the
                // purchase; this blocks equipping over-leveled gear
                // acquired through any other channel: mail, bank
                // withdrawal, pre-gate inventory).
                int requiredLevel = EquipmentLevelGate.DeriveRequiredLevel(item.BaseItemId, item.QualityTier);
                if (playerLevel < requiredLevel)
                {
                    await transaction.RollbackAsync();
                    Console.WriteLine($"Equip rejected: player {playerId} level {playerLevel} below required {requiredLevel} for {item.BaseItemId} T{item.QualityTier}.");
                    _playerRegistry?.EnqueueCommandResult(playerId, (byte)FolkIdle.Server.Network.CommandResultCode.LevelTooLow);
                    return;
                }

                WriteSlot(character, slotIndex, item.Id);

                await db.SaveChangesAsync();

                EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, character);

                await transaction.CommitAsync();

                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Equip item failed for player {playerId}: {ex.Message}");
            }
        }

        public async Task UnequipItemAsync(long playerId, int slotIndex, Guid characterId = default)
        {
            using var scope = _serviceProvider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FolkIdleDbContext>();
            using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            try
            {
                if (slotIndex < 0 || slotIndex >= SlotCount)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                var character = await ResolveCharacterForUpdateAsync(db, playerId, characterId);
                if (character == null)
                {
                    await transaction.RollbackAsync();
                    return;
                }

                WriteSlot(character, slotIndex, null);

                await db.SaveChangesAsync();

                EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, character);

                await transaction.CommitAsync();

                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(notification);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync();
                Console.WriteLine($"Unequip item failed for player {playerId}: {ex.Message}");
            }
        }

        // Locks the target character's row. Guid.Empty resolves to the main
        // character, which is the one whose Id equals PlayerRecords.PlayerGuid -
        // the same character StateCheckpointManager hydrates into slot 1.
        private static async Task<CharacterRecord?> ResolveCharacterForUpdateAsync(FolkIdleDbContext db, long playerId, Guid characterId)
        {
            if (characterId == Guid.Empty)
            {
                var mainCharacterId = await db.PlayerRecords.AsNoTracking()
                    .Where(p => p.Id == playerId)
                    .Select(p => p.PlayerGuid)
                    .FirstOrDefaultAsync();

                if (mainCharacterId == Guid.Empty)
                {
                    return null;
                }
                characterId = mainCharacterId;
            }

            var characters = await db.CharacterRecords
                .FromSqlInterpolated($"SELECT * FROM \"characters\" WHERE \"Id\" = {characterId} FOR UPDATE")
                .ToListAsync();

            var character = characters.Count > 0 ? characters[0] : null;
            return character != null && character.PlayerId == playerId ? character : null;
        }

        // Re-derives the character's combined equipped-gear stat totals from
        // whichever slots are currently filled, so both EquipItemAsync and
        // UnequipItemAsync always report a fully consistent snapshot regardless
        // of which single slot just changed.
        private static async Task<EquipmentSlotUpdateNotification> BuildNotificationAsync(FolkIdleDbContext db, CharacterRecord character)
        {
            (EquippedAffixTotals totals, int weaponSetId, int armorSetId, int leggingsSetId) = await ComputeEquippedTotalsAsync(db, character);

            return new EquipmentSlotUpdateNotification
            {
                PlayerId = character.PlayerId,
                CharacterId = character.Id,
                EquippedWeaponId = character.EquippedWeaponId ?? 0L,
                EquippedHelmetId = character.EquippedHelmetId ?? 0L,
                EquippedChestId = character.EquippedChestId ?? 0L,
                EquippedGlovesId = character.EquippedGlovesId ?? 0L,
                EquippedLeggingsId = character.EquippedLeggingsId ?? 0L,
                EquippedBootsId = character.EquippedBootsId ?? 0L,
                AffixTotals = totals,
                EquippedWeaponSetId = weaponSetId,
                EquippedArmorSetId = armorSetId,
                EquippedLeggingsSetId = leggingsSetId
            };
        }

        // Shared with StateCheckpointManager.LoadPlayerState, which needs the
        // same combined totals at login time for EVERY character it hydrates:
        // the equipped ids are persisted but the derived stat totals are not, so
        // they must be recomputed once per character rather than reading
        // stale/zeroed values until that character's next equip action.
        //
        // One query for all six slots instead of one per slot - six sequential
        // round trips per character, times three characters, on every login was
        // not worth the marginally simpler code.
        public static async Task<(EquippedAffixTotals Totals, int WeaponSetId, int ArmorSetId, int LeggingsSetId)> ComputeEquippedTotalsAsync(FolkIdleDbContext db, CharacterRecord character)
        {
            EquippedAffixTotals totals = default;
            int weaponSetId = 0, armorSetId = 0, leggingsSetId = 0;

            long weaponId = character.EquippedWeaponId ?? 0L;
            long helmetId = character.EquippedHelmetId ?? 0L;
            long chestId = character.EquippedChestId ?? 0L;
            long glovesId = character.EquippedGlovesId ?? 0L;
            long leggingsId = character.EquippedLeggingsId ?? 0L;
            long bootsId = character.EquippedBootsId ?? 0L;

            if (weaponId == 0L && helmetId == 0L && chestId == 0L && glovesId == 0L && leggingsId == 0L && bootsId == 0L)
            {
                return (totals, 0, 0, 0);
            }

            var worn = await db.EquipmentInstances
                .AsNoTracking()
                .Where(e => e.Id == weaponId || e.Id == helmetId || e.Id == chestId || e.Id == glovesId || e.Id == leggingsId || e.Id == bootsId)
                .ToListAsync();

            for (int i = 0; i < worn.Count; i++)
            {
                var piece = worn[i];
                AddAffixTotals(piece.AffixPayload, ref totals);

                // Set ids stay a weapon/armour/leggings triple because that is
                // what SetBonusEngine.Evaluate consumes. The four armour slots
                // collapse onto the single armour set id, taking the first one
                // found - widening set bonuses to six slots is a balance change,
                // not a refactor, and does not belong in this pass.
                if (piece.Id == weaponId) weaponSetId = piece.SetId;
                else if (piece.Id == leggingsId) leggingsSetId = piece.SetId;
                else if (armorSetId == 0) armorSetId = piece.SetId;
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
