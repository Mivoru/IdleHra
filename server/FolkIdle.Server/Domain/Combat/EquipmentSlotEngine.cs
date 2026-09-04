using System;
using System.Collections.Generic;
using System.Linq;
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
        // Modul: THERE IS NO OFFHAND, and adding one was the mistake.
        //
        // A previous pass introduced SlotOffhand as "the seventh slot, closing
        // the same gap the six-slot pass closed" and authored five helper items
        // to fill it. The design has no offhand: the slots are weapon, the five
        // armour pieces, an amulet and a ring. The five helper items were
        // invented to justify a slot that was itself invented, which is why
        // none of them has artwork - they were never on the asset list.
        //
        // Amulet and Ring are the two that were actually missing. Their items
        // have existed all along, correctly, one of each per tier
        // (eq_linen_pendant / eq_copper_band up to eq_doom_gorget /
        // eq_dread_signet), and ResolveSlotIndex returned -1 for every one of
        // them - so a player could loot an amulet and never put it on.
        public const int SlotAmulet = 6;
        public const int SlotRing = 7;

        // Modul: TOOLS ARE WORN. They were stackable materials sitting in the
        // chest, which is why every axe in the game was identical to every
        // other axe of the same wood - a stack has no room for a rarity or an
        // affix. Three slots rather than one, because a character carries an
        // axe, a pickaxe and a rod at the same time and each accelerates its
        // own profession.
        public const int SlotAxe = 8;
        public const int SlotPickaxe = 9;
        public const int SlotRod = 10;

        // Eight worn slots plus the three tools.
        public const int SlotCount = 11;

        /// <summary>The worn slots, excluding the three tools. Ring is the last.</summary>
        public const int LastGearSlot = SlotRing;

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
            // Tools first: a "_tool" id carries none of the armour or weapon
            // markers below, so order is not load-bearing here - but keeping it
            // at the top says plainly that a tool is its own thing rather than
            // a weapon that happens to chop.
            if (baseItemId != null && baseItemId.EndsWith("_tool", StringComparison.Ordinal))
            {
                if (baseItemId.Contains("_pickaxe_", StringComparison.Ordinal)) return SlotPickaxe;
                if (baseItemId.Contains("_fishing_rod_", StringComparison.Ordinal)) return SlotRod;
                if (baseItemId.Contains("_axe_", StringComparison.Ordinal)) return SlotAxe;
            }

            if (string.IsNullOrEmpty(baseItemId)) return -1;

            if (baseItemId.Contains("_helmet_")) return SlotHelmet;
            if (baseItemId.Contains("_gloves_")) return SlotGloves;
            if (baseItemId.Contains("_boots_")) return SlotBoots;
            if (baseItemId.Contains("_leggings_")) return SlotLeggings;
            if (baseItemId.Contains("_chest_")) return SlotChest;
            // Jewellery, which used to fall through to -1. Ten authored items -
            // one amulet and one ring per tier - that a player could loot and
            // never wear. Both markers are tested before the weapon and
            // generic-armour checks for the same reason every specific marker
            // is: an id carrying two markers must match the specific one.
            if (baseItemId.Contains("_amulet_")) return SlotAmulet;
            if (baseItemId.Contains("_ring_")) return SlotRing;
            if (baseItemId.Contains("_weapon_slot_")) return SlotWeapon;
            if (baseItemId.Contains("_armor_slot_")) return SlotChest;

            // Modul: "_helper_offhand_" used to resolve here, to a slot that
            // should not exist. It returns -1 now, deliberately: any surviving
            // helper item is unequippable rather than silently filling a slot
            // the design does not have.
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
            SlotAmulet => character.EquippedAmuletId,
            SlotRing => character.EquippedRingId,
            // Modul: completed to match WriteSlot, which has handled the three
            // tool slots all along. A reader that silently answers "empty" for
            // a slot its writer can fill is the shape of the bug that made a
            // worn tool invisible; this one has no callers today, which is the
            // only reason it did not cause a second one.
            SlotAxe => character.EquippedAxeId,
            SlotPickaxe => character.EquippedPickaxeId,
            SlotRod => character.EquippedRodId,
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
                case SlotAmulet: character.EquippedAmuletId = itemInstanceId; break;
                case SlotRing: character.EquippedRingId = itemInstanceId; break;
                case SlotAxe: character.EquippedAxeId = itemInstanceId; break;
                case SlotPickaxe: character.EquippedPickaxeId = itemInstanceId; break;
                case SlotRod: character.EquippedRodId = itemInstanceId; break;
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
                    c.EquippedBootsId == itemInstanceId ||
                    c.EquippedAmuletId == itemInstanceId ||
                    c.EquippedRingId == itemInstanceId ||
                    // Modul: the three tool slots were missing from this test,
                    // so the "one physical item cannot be worn by two
                    // characters at once" rule this method exists to enforce
                    // did not cover tools at all - a single axe could be worn
                    // by the whole roster and count its bonus three times.
                    c.EquippedAxeId == itemInstanceId ||
                    c.EquippedPickaxeId == itemInstanceId ||
                    c.EquippedRodId == itemInstanceId));
        }

        // Three-item variant for ForgeSplicingEngine, which locks a target and
        // two sacrifices together and would otherwise need three round trips
        // inside one Serializable transaction.
        /// <summary>
        /// Clears equip pointers to EquipmentInstances that no longer exist.
        ///
        /// Modul: found by the paper doll. The dev fixture's main character had
        /// seven pieces on its row and only three of them were rows in
        /// EquipmentInstances - it was "wearing" four items that had been
        /// deleted out from under it. The screen rendered three filled slots
        /// and four empty ones, which is the truth, and looked like a bug in
        /// the screen.
        ///
        /// A dangling pointer is not merely cosmetic: every stat recompute
        /// walks these fields, so the character silently loses the armour it
        /// believes it is wearing with nothing anywhere saying so.
        ///
        /// Fixing the writers that can orphan a row is the real cure and the
        /// forge already guards against it; this is the sweep that heals rows
        /// already broken, and it runs at hydration where the cost is one query
        /// per login rather than one per tick.
        /// </summary>
        public static async Task<int> ClearDanglingEquipReferencesAsync(FolkIdleDbContext db, long playerId)
        {
            var characters = await db.CharacterRecords.Where(c => c.PlayerId == playerId).ToListAsync();
            if (characters.Count == 0) return 0;

            var referenced = new HashSet<long>();
            foreach (var character in characters)
            {
                void Note(long? id) { if (id.HasValue) referenced.Add(id.Value); }
                Note(character.EquippedWeaponId);
                Note(character.EquippedHelmetId);
                Note(character.EquippedChestId);
                Note(character.EquippedGlovesId);
                Note(character.EquippedLeggingsId);
                Note(character.EquippedBootsId);
                Note(character.EquippedAmuletId);
                Note(character.EquippedRingId);
            }

            if (referenced.Count == 0) return 0;

            var alive = (await db.EquipmentInstances
                .AsNoTracking()
                .Where(e => referenced.Contains(e.Id))
                .Select(e => e.Id)
                .ToListAsync()).ToHashSet();

            int cleared = 0;
            foreach (var character in characters)
            {
                long? Keep(long? id)
                {
                    if (!id.HasValue || alive.Contains(id.Value)) return id;
                    cleared++;
                    return null;
                }

                character.EquippedWeaponId = Keep(character.EquippedWeaponId);
                character.EquippedHelmetId = Keep(character.EquippedHelmetId);
                character.EquippedChestId = Keep(character.EquippedChestId);
                character.EquippedGlovesId = Keep(character.EquippedGlovesId);
                character.EquippedLeggingsId = Keep(character.EquippedLeggingsId);
                character.EquippedBootsId = Keep(character.EquippedBootsId);
                character.EquippedAmuletId = Keep(character.EquippedAmuletId);
                character.EquippedRingId = Keep(character.EquippedRingId);
            }

            if (cleared > 0) await db.SaveChangesAsync();
            return cleared;
        }

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
                    (c.EquippedBootsId != null && (c.EquippedBootsId == firstItemId || c.EquippedBootsId == secondItemId || c.EquippedBootsId == thirdItemId)) ||
                    (c.EquippedAmuletId != null && (c.EquippedAmuletId == firstItemId || c.EquippedAmuletId == secondItemId || c.EquippedAmuletId == thirdItemId)) ||
                    (c.EquippedRingId != null && (c.EquippedRingId == firstItemId || c.EquippedRingId == secondItemId || c.EquippedRingId == thirdItemId))));
        }

        // Modul: per-character equipment. characterId names WHICH of the
        // player's characters is putting the item on. Guid.Empty means "the
        // main character", so a client that has not been taught about the
        // roster yet keeps working exactly as before.
        // Modul: retryable equip. The outcome of one attempt, so the retriable
        // delegate can report what happened without performing side effects
        // itself. A delegate handed to an execution strategy may run more than
        // once, so anything that must happen exactly once - pushing a command
        // result to the player, enqueuing a slot update for the tick thread -
        // has to sit outside it, keyed off this.
        private readonly struct EquipAttemptOutcome
        {
            public readonly bool Committed;
            public readonly byte? ResultCode;
            public readonly EquipmentSlotUpdateNotification Notification;

            public EquipAttemptOutcome(bool committed, byte? resultCode, EquipmentSlotUpdateNotification notification)
            {
                Committed = committed;
                ResultCode = resultCode;
                Notification = notification;
            }

            // Modul: a rejection with no code reported NOTHING to the client -
            // PublishOutcome only enqueues a result when ResultCode has a
            // value - so three of the four rejection paths in EquipItemAsync
            // (unknown item, unequippable BaseItemId, unresolvable character)
            // were completely silent: the button did nothing, no toast, and no
            // server log line either. That is this codebase's most-repeated
            // bug shape, and it cost a full debugging session on 2026-08-02
            // before the real cause turned out to be a fixture whose main
            // character Id did not match its PlayerGuid.
            //
            // GenericValidationFailure is deliberately vague - these paths
            // genuinely do not know more than "that was not valid" - but vague
            // is enormously better than silent, and it is the difference
            // between a player reporting "equip is broken" and reporting
            // nothing at all because nothing appeared to happen.
            public static EquipAttemptOutcome Rejected(byte? resultCode = null) =>
                new(false, resultCode ?? (byte)FolkIdle.Server.Network.CommandResultCode.GenericValidationFailure, default);
            public static EquipAttemptOutcome Success(EquipmentSlotUpdateNotification notification) => new(true, null, notification);
        }

        // Modul: retryable equip. Equipping several pieces in quick succession
        // makes them contend for the same character row's FOR UPDATE lock, and
        // a loser used to get "a transient failure" that the catch below
        // swallowed - the item silently stayed unequipped with no feedback at
        // all. Two fast clicks in a real session did the same thing, and a live
        // Play Mode run lost four of ten equips to it.
        //
        // The fix is the pattern CraftingEngine already uses: a
        // retry-configured context plus an execution strategy wrapping the whole
        // transaction, so a Serializable conflict is retried as a unit. A
        // retrying context WITHOUT the strategy wrapper is not a partial fix but
        // a regression - EF refuses user-initiated transactions under one and
        // throws on every single equip.
        //
        // The delegate is re-runnable: it clears the change tracker on entry so
        // a retry does not inherit a half-applied graph, and it returns its
        // outcome instead of enqueuing anything.
        public async Task EquipItemAsync(long playerId, long itemInstanceId, Guid characterId = default)
        {
            await using var db = new FolkIdleDbContext(_serviceProvider.GetRequiredService<RetryingDbContextOptions>().Options);
            var strategy = db.Database.CreateExecutionStrategy();

            EquipAttemptOutcome outcome;
            try
            {
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    var item = await db.EquipmentInstances
                        .FromSqlInterpolated($"SELECT * FROM \"EquipmentInstances\" WHERE \"Id\" = {itemInstanceId} FOR UPDATE")
                        .SingleOrDefaultAsync();

                    if (item == null || item.PlayerId != playerId)
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected();
                    }

                    int slotIndex = ResolveSlotIndex(item.BaseItemId);
                    if (slotIndex < 0)
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected();
                    }

                    var character = await ResolveCharacterForUpdateAsync(db, playerId, characterId);
                    if (character == null)
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected();
                    }

                    // Modul: per-character equipment. One physical item cannot be
                    // worn by two characters at once. Without this, equipping the
                    // same sword on all three would triple its stats across the
                    // roster from a single drop.
                    if (await IsEquippedAnywhereAsync(db, playerId, itemInstanceId))
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected((byte)FolkIdle.Server.Network.CommandResultCode.ItemEquipped);
                    }

                    // Modul: region gate at equip time, replacing the level
                    // gate. Still the second half of the anti-cheese lock -
                    // MarketEscrowEngine.BuyItemAsync blocks the purchase, this
                    // blocks wearing gear acquired through any other channel
                    // (mail, bank withdrawal, pre-gate inventory) - but the
                    // question it asks changed. It used to be "are you high
                    // enough level for something this rare", which refused a
                    // region-1 Epic to the only characters who could farm it.
                    // It is now "have you opened the region this came from",
                    // and rarity is not part of it: inside an open region every
                    // rarity is wearable.
                    var defeatedBosses = await RegionUnlockGate.LoadDefeatedBossesAsync(db, playerId);
                    if (!RegionUnlockGate.CanWearItem(item.BaseItemId, defeatedBosses))
                    {
                        await transaction.RollbackAsync();
                        Console.WriteLine($"Equip rejected: player {playerId} has not unlocked the region for {item.BaseItemId} (highest unlocked {RegionUnlockGate.HighestUnlockedRegion(defeatedBosses)}).");
                        return EquipAttemptOutcome.Rejected((byte)FolkIdle.Server.Network.CommandResultCode.RegionLocked);
                    }

                    WriteSlot(character, slotIndex, item.Id);

                    await db.SaveChangesAsync();

                    EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, character);

                    await transaction.CommitAsync();

                    return EquipAttemptOutcome.Success(notification);
                });
            }
            catch (Exception ex)
            {
                // Reached only once the strategy has exhausted its retries or
                // hit something it does not consider transient.
                Console.WriteLine($"Equip item failed for player {playerId}: {ex.Message}");
                return;
            }

            PublishOutcome(playerId, outcome);
        }

        public async Task UnequipItemAsync(long playerId, int slotIndex, Guid characterId = default)
        {
            await using var db = new FolkIdleDbContext(_serviceProvider.GetRequiredService<RetryingDbContextOptions>().Options);
            var strategy = db.Database.CreateExecutionStrategy();

            EquipAttemptOutcome outcome;
            try
            {
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    db.ChangeTracker.Clear();
                    using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);

                    if (slotIndex < 0 || slotIndex >= SlotCount)
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected();
                    }

                    var character = await ResolveCharacterForUpdateAsync(db, playerId, characterId);
                    if (character == null)
                    {
                        await transaction.RollbackAsync();
                        return EquipAttemptOutcome.Rejected();
                    }

                    WriteSlot(character, slotIndex, null);

                    await db.SaveChangesAsync();

                    EquipmentSlotUpdateNotification notification = await BuildNotificationAsync(db, character);

                    await transaction.CommitAsync();

                    return EquipAttemptOutcome.Success(notification);
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Unequip item failed for player {playerId}: {ex.Message}");
                return;
            }

            PublishOutcome(playerId, outcome);
        }

        // The exactly-once half of both operations, kept out of the retriable
        // delegate so a retry cannot double-report a rejection or push the same
        // slot update to the tick thread twice.
        private void PublishOutcome(long playerId, EquipAttemptOutcome outcome)
        {
            if (outcome.ResultCode.HasValue)
            {
                _playerRegistry?.EnqueueCommandResult(playerId, outcome.ResultCode.Value);
            }

            if (outcome.Committed)
            {
                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(outcome.Notification);
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

        /// <summary>
        /// Which of the three weapon families a character is swinging: 0 melee,
        /// 1 ranged, 2 magic, 0 for an empty hand.
        ///
        /// EXISTS FOR THE HIT EFFECT. The client draws a slash, an arrow or a
        /// burst depending on this, and it has no other way to know - the
        /// packet carries an equipment INSTANCE id, and resolving that to a
        /// base item would mean an inventory fetch on the combat screen just to
        /// decide which animation to play.
        ///
        /// Matched on the same substrings AffixRegistry and EquipmentDropTable
        /// use. Note "_range_weapon_slot_" with no "d" - the canonical bows are
        /// authored that way, and grepping for "_ranged_" is the exact typo
        /// that once made every bow in the game undroppable.
        /// </summary>
        public static byte ResolveWeaponKind(string? baseItemId)
        {
            if (string.IsNullOrEmpty(baseItemId)) return 0;
            if (baseItemId.Contains("_range_weapon_slot", StringComparison.Ordinal)) return 1;
            if (baseItemId.Contains("_magic_weapon_slot", StringComparison.Ordinal)) return 2;
            return 0;
        }

        /// <summary>The weapon kind a character is currently holding.</summary>
        public static async Task<byte> ResolveEquippedWeaponKindAsync(FolkIdleDbContext db, CharacterRecord? character)
        {
            long weaponId = character?.EquippedWeaponId ?? 0L;
            if (weaponId <= 0) return 0;

            string? baseItemId = await db.EquipmentInstances
                .AsNoTracking()
                .Where(e => e.Id == weaponId)
                .Select(e => e.BaseItemId)
                .FirstOrDefaultAsync();

            return ResolveWeaponKind(baseItemId);
        }

        private static async Task<EquipmentSlotUpdateNotification> BuildNotificationAsync(FolkIdleDbContext db, CharacterRecord character)
        {
            (EquippedAffixTotals totals, EquippedSetIds setIds) = await ComputeEquippedTotalsAsync(db, character);

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
                EquippedAmuletId = character.EquippedAmuletId ?? 0L,
                EquippedRingId = character.EquippedRingId ?? 0L,
                AffixTotals = totals,
                SetIds = setIds,
                EquippedWeaponKind = await ResolveEquippedWeaponKindAsync(db, character)
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
        public static async Task<(EquippedAffixTotals Totals, EquippedSetIds SetIds)> ComputeEquippedTotalsAsync(FolkIdleDbContext db, CharacterRecord character)
        {
            EquippedAffixTotals totals = default;
            EquippedSetIds setIds = default;

            long weaponId = character.EquippedWeaponId ?? 0L;
            long helmetId = character.EquippedHelmetId ?? 0L;
            long chestId = character.EquippedChestId ?? 0L;
            long glovesId = character.EquippedGlovesId ?? 0L;
            long leggingsId = character.EquippedLeggingsId ?? 0L;
            long bootsId = character.EquippedBootsId ?? 0L;
            long amuletId = character.EquippedAmuletId ?? 0L;
            long ringId = character.EquippedRingId ?? 0L;

            if (weaponId == 0L && helmetId == 0L && chestId == 0L && glovesId == 0L && leggingsId == 0L && bootsId == 0L && amuletId == 0L && ringId == 0L)
            {
                return (totals, setIds);
            }

            var worn = await db.EquipmentInstances
                .AsNoTracking()
                .Where(e => e.Id == weaponId || e.Id == helmetId || e.Id == chestId || e.Id == glovesId || e.Id == leggingsId || e.Id == bootsId || e.Id == amuletId || e.Id == ringId)
                .ToListAsync();

            for (int i = 0; i < worn.Count; i++)
            {
                var piece = worn[i];
                AddAffixTotals(piece.AffixPayload, ref totals);

                // Modul: balance pass. An item's OWN base power - the
                // FlatAttackPower/FlatDefenseRating authored in items.json,
                // which triples every region tier (weapons 12/36/108/324/972,
                // chest armour 8/24/72/216/648) - used to be read by nothing at
                // all. Only affixes and the set bonus reached StatsCalculator,
                // so a tier-5 Doom Edge hit exactly as hard as a tier-1 Steel
                // Claymore and the entire gear progression was cosmetic. This is
                // the same "the output side was never wired" shape as the
                // crafting-grants-nothing and loot-table bugs; it is the single
                // largest instance because it silently flattened the whole
                // power curve, which in turn made the exponential XP curve
                // unreachable from region 3 onward.
                //
                // Base power folds into the SAME totals the affixes use, so
                // StatsCalculator needs no new parameter and the value rides
                // the existing notification/payload path unchanged.
                // Modul: and the item's QUALITY tier scales that base power.
                //
                // It contributed zero until 2026-09-04, which is why the whole
                // fourteen-tier ladder was worth 1.48x against a region step's
                // 3.00x - see RarityTier.PowerMultiplier for the measurement
                // and the decision. Applied here, at the one place base power
                // is read, so there is a single authority over what a tier is
                // worth.
                if (ContentRegistry.TryGetItemDefinitionByBaseId(piece.BaseItemId, out var itemDefinition))
                {
                    double qualityMultiplier = RarityTier.PowerMultiplier(piece.QualityTier);
                    totals.FlatAttack += (int)System.Math.Round(itemDefinition.FlatAttackPower * qualityMultiplier);
                    totals.FlatDefense += (int)System.Math.Round(itemDefinition.FlatDefenseRating * qualityMultiplier);
                }

                // Modul: seven-slot set bonuses. Every worn piece now reports its
                // own SetId. This used to be a weapon/armour/leggings triple
                // that folded helmet, chest, gloves and boots onto ONE "armor"
                // id, taking whichever was seen first and discarding the other
                // three - so SetBonusEngine, which awards its tiers by counting
                // how many equipped pieces share a set, could never see a count
                // above 3 and no 4-piece bonus in the game was reachable.
                // Resolved by slot rather than by guessing, so a matching set
                // counts once per piece actually worn.
                int pieceSlotIndex =
                      piece.Id == weaponId ? SlotWeapon
                    : piece.Id == helmetId ? SlotHelmet
                    : piece.Id == chestId ? SlotChest
                    : piece.Id == glovesId ? SlotGloves
                    : piece.Id == leggingsId ? SlotLeggings
                    : piece.Id == bootsId ? SlotBoots
                    : piece.Id == amuletId ? SlotAmulet
                    : piece.Id == ringId ? SlotRing
                    : -1;

                if (pieceSlotIndex >= 0)
                {
                    setIds.SetBySlotIndex(pieceSlotIndex, piece.SetId, piece.QualityTier);
                }
            }

            return (totals, setIds);
        }

        // Modul: paper-doll combat rating, per roster character. Mirrors
        // GuildWarSnapshotEngine.BuildMemberCombatStatsAsync's race/age/
        // genetics/gear resolution, but for a CALLER-SUPPLIED character
        // instead of always the account's first one, and taking the
        // account-wide mastery/completion inputs already fetched once by the
        // caller rather than re-querying them per roster slot.
        //
        // Before this, PlayerAccuracyRating/PlayerArmorRating/
        // PlayerBlockStrengthPct on StateUpdate were the ONLY combat rating
        // numbers anywhere - and that packet is deliberately the ACTIVE
        // character's only (see StateUpdatePacket's own comment on why gear
        // stays off the hot path for slots 2/3). The Character screen showed
        // those three numbers unchanged under every paper-doll tab, which
        // reads as "your gear does nothing" the moment two characters wear
        // different weapons.
        public static async Task<CombatStats> ComputeCharacterCombatStatsAsync(
            FolkIdleDbContext db,
            PlayerRecord player,
            CharacterRecord character,
            int humanMastery,
            int vilaMastery,
            int draugrMastery,
            int completedAreaFlags)
        {
            int activeAgePhase = character.AgePhase;
            int activeRaceId = 0;
            bool isEpicMutation = false;
            int locusSpeed = 0;
            int locusCrit = 0;

            if (character.Lineage != null)
            {
                activeRaceId = (int)(character.Lineage.GeneticVector & 0xFF);
                isEpicMutation = character.Lineage.IsEpicMutation;
                var geneVec = new GeneticVector(character.Lineage.GeneticVector);
                locusSpeed = geneVec.LocusSpeed.Dominant;
                locusCrit = geneVec.LocusCrit.Dominant;
            }

            (EquippedAffixTotals totals, EquippedSetIds setIds) = await ComputeEquippedTotalsAsync(db, character);

            return StatsCalculator.Calculate(
                player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck,
                player.ActiveOffensivePotionId, player.ActiveDefensivePotionId,
                activeAgePhase, completedAreaFlags, activeRaceId,
                humanMastery, vilaMastery, draugrMastery,
                totals, isEpicMutation, locusSpeed, locusCrit, setIds);
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
