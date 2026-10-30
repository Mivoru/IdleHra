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

        internal static void WriteSlot(CharacterRecord character, int slotIndex, long? itemInstanceId)
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

            // Modul: ALL ELEVEN SLOTS (2026-10-20). This walked the eight combat
            // slots only, so a worn tool whose row was gone kept its pointer -
            // the truncation CLAUDE.md warns about, in the sweep that exists to
            // heal exactly this.
            var referenced = new HashSet<long>();
            foreach (var character in characters)
            {
                for (int slot = 0; slot < SlotCount; slot++)
                {
                    long? id = ReadSlot(character, slot);
                    if (id.HasValue) referenced.Add(id.Value);
                }
            }

            int cleared = 0;
            if (referenced.Count > 0)
            {
                var alive = (await db.EquipmentInstances
                    .AsNoTracking()
                    .Where(e => referenced.Contains(e.Id))
                    .Select(e => e.Id)
                    .ToListAsync()).ToHashSet();

                foreach (var character in characters)
                {
                    for (int slot = 0; slot < SlotCount; slot++)
                    {
                        long? id = ReadSlot(character, slot);
                        if (!id.HasValue || alive.Contains(id.Value)) continue;
                        WriteSlot(character, slot, null);
                        cleared++;
                    }
                }
            }

            // Modul: A BENCHED CHARACTER WEARS NOTHING (2026-10-20). Gear
            // belongs to the three playable slots, not to whoever once stood in
            // them - HallOfAncestorsEngine.AssignSlotAsync hands it to the
            // character who takes the slot. Before that, a character benched
            // kept everything it wore, and the Character screen listed it as
            // "worn by another character" with Move disabled, because the
            // wearer was nobody it could show. Measured live: ten benched
            // characters across three accounts held full tool sets and armour.
            // This heals those rows; the swap keeps new ones from appearing.
            foreach (var character in characters)
            {
                if (character.SlotIndex < CharacterSlotEngine.MaxCharacterSlots) continue;
                cleared += StripAllSlots(character);
            }

            if (cleared > 0) await db.SaveChangesAsync();
            return cleared;
        }

        /// <summary>Empties all eleven slots; returns how many held something.</summary>
        internal static int StripAllSlots(CharacterRecord character)
        {
            int stripped = 0;
            for (int slot = 0; slot < SlotCount; slot++)
            {
                if (!ReadSlot(character, slot).HasValue) continue;
                WriteSlot(character, slot, null);
                stripped++;
            }
            return stripped;
        }

        /// <summary>Exchanges all eleven slots between two characters.</summary>
        internal static void SwapAllSlots(CharacterRecord first, CharacterRecord second)
        {
            for (int slot = 0; slot < SlotCount; slot++)
            {
                long? held = ReadSlot(first, slot);
                WriteSlot(first, slot, ReadSlot(second, slot));
                WriteSlot(second, slot, held);
            }
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
                    (c.EquippedRingId != null && (c.EquippedRingId == firstItemId || c.EquippedRingId == secondItemId || c.EquippedRingId == thirdItemId)) ||
                    // Modul: the three tool slots, missing here like in every
                    // list that stopped at EquippedRingId - a worn tool could be
                    // fed to the forge from under the character wearing it.
                    (c.EquippedAxeId != null && (c.EquippedAxeId == firstItemId || c.EquippedAxeId == secondItemId || c.EquippedAxeId == thirdItemId)) ||
                    (c.EquippedPickaxeId != null && (c.EquippedPickaxeId == firstItemId || c.EquippedPickaxeId == secondItemId || c.EquippedPickaxeId == thirdItemId)) ||
                    (c.EquippedRodId != null && (c.EquippedRodId == firstItemId || c.EquippedRodId == secondItemId || c.EquippedRodId == thirdItemId))));
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

            // Modul: this used to report ResultCode = null on purpose - only
            // rejections were meant to say anything, back when nothing
            // downstream cared about a SUCCESSFUL equip's timing. Now
            // Character.svelte's cache invalidation is driven by this same
            // CommandResult ring buffer (game.ts's processCommandResults)
            // instead of a guessed-delay setTimeout, so a successful equip or
            // unequip has to report too, or the client never hears the
            // command resolved and falls back to the stale timer this fix
            // removes.
            public static EquipAttemptOutcome Success(EquipmentSlotUpdateNotification notification) =>
                new(true, (byte)FolkIdle.Server.Network.CommandResultCode.Success, notification);
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
            int attempts = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    attempts++;
                    db.ChangeTracker.Clear();
                    // Modul: READ COMMITTED + THE ROSTER LOCKED, NOT SERIALIZABLE
                    // (2026-10-20). "Equipping is so slow and laggy I had to
                    // restart." Serializable made every read below a conflict
                    // with a row the game rewrites constantly - the codex boss
                    // kills (CodexEngine, every kill batch) and PlayerRecords
                    // (the checkpoint) - so an equip during a fight failed at
                    // commit and the execution strategy retried it, silently,
                    // with random back-off up to 8 s a time, six times. What
                    // Serializable actually protected is "one piece, one
                    // wearer", and locking every character row of the account
                    // protects that directly: a second equip waits for this one
                    // instead of racing it. SlowEquipLog says when it is slow.
                    using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                    await LockRosterAsync(db, playerId);

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

                    // Modul: AND WHAT THE ITEM ASKS OF THE CHARACTER, 2026-09-06.
                    //
                    // Attributes had identities, curves and milestone tracks and
                    // still no consequence - a pure-Vigour character could wield
                    // the best weapon in the game, so specialising only ever
                    // meant "and also get everything else anyway". A minimum on
                    // the item is the one rule that makes the choice binding
                    // without taking anything from anyone.
                    //
                    // Beside the region gate rather than anywhere else because
                    // this is the same question asked twice: may this character
                    // wear this thing. Checked ONLY here, never on the tick -
                    // see EquipmentAttributeGate for why nothing already worn is
                    // ever stripped.
                    var wearer = await db.PlayerRecords
                        .AsNoTracking()
                        .Where(p => p.Id == playerId)
                        .Select(p => new { p.BaseStrength, p.BaseDexterity, p.BaseConstitution, p.BaseLuck })
                        .SingleOrDefaultAsync();

                    if (wearer != null && !EquipmentAttributeGate.CanWear(
                            item.BaseItemId, wearer.BaseStrength, wearer.BaseDexterity,
                            wearer.BaseConstitution, wearer.BaseLuck))
                    {
                        var (needed, minimum) = EquipmentAttributeGate.RequirementOf(item.BaseItemId);
                        await transaction.RollbackAsync();
                        Console.WriteLine(
                            $"Equip rejected: player {playerId} needs {minimum} {AttributeRegistry.NameOf(needed)} for {item.BaseItemId}.");
                        return EquipAttemptOutcome.Rejected(
                            (byte)FolkIdle.Server.Network.CommandResultCode.AttributeRequirementNotMet);
                    }

                    // Modul: THE VAULT LOCK (2026-10-20). A piece kept through
                    // a rebirth waits until this life has killed in its region
                    // - see EquipmentInstance.VaultLockRegion. Read here, after
                    // the other gates, and cleared on the equip that passes it.
                    if (item.VaultLockRegion > 0)
                    {
                        int lifeRegion = await db.PlayerRecords.AsNoTracking()
                            .Where(p => p.Id == playerId)
                            .Select(p => p.LifeHighestRegion)
                            .SingleOrDefaultAsync();
                        if (lifeRegion < item.VaultLockRegion)
                        {
                            await transaction.RollbackAsync();
                            return EquipAttemptOutcome.Rejected((byte)FolkIdle.Server.Network.CommandResultCode.VaultLocked);
                        }
                        item.VaultLockRegion = 0;
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

            SlowEquipLog("Equip", playerId, attempts, clock);

            // Funnel step 3: only a committed equip counts. See FunnelRecorder.
            if (outcome.Committed) FunnelRecorder.Record(playerId, FunnelStep.FirstEquip);

            await PublishOutcomeAsync(db, playerId, outcome);
        }

        public async Task UnequipItemAsync(long playerId, int slotIndex, Guid characterId = default)
        {
            await using var db = new FolkIdleDbContext(_serviceProvider.GetRequiredService<RetryingDbContextOptions>().Options);
            var strategy = db.Database.CreateExecutionStrategy();

            EquipAttemptOutcome outcome;
            int attempts = 0;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                outcome = await strategy.ExecuteAsync(async () =>
                {
                    attempts++;
                    db.ChangeTracker.Clear();
                    // Same isolation as EquipItemAsync, and why.
                    using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted);
                    await LockRosterAsync(db, playerId);

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

            SlowEquipLog("Unequip", playerId, attempts, clock);

            await PublishOutcomeAsync(db, playerId, outcome);
        }

        /// <summary>
        /// Locks every character row of the account, in a fixed order, for the
        /// rest of the transaction - the per-account serialisation an equip
        /// needs ("one piece, one wearer" is a question about all of them).
        /// </summary>
        private static async Task LockRosterAsync(FolkIdleDbContext db, long playerId)
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"characters\" WHERE \"PlayerId\" = {playerId} ORDER BY \"Id\" FOR UPDATE");
        }

        // A grep-able line when an equip took long enough to feel, with how many
        // attempts the execution strategy needed - retries are otherwise silent.
        private static void SlowEquipLog(string what, long playerId, int attempts, System.Diagnostics.Stopwatch clock)
        {
            if (clock.ElapsedMilliseconds < 750 && attempts <= 1) return;
            Console.WriteLine($"[equip-slow] {what} for player {playerId}: {clock.ElapsedMilliseconds} ms, {attempts} attempt(s)");
        }

        // The exactly-once half of both operations, kept out of the retriable
        // delegate so a retry cannot double-report a rejection or push the same
        // slot update to the tick thread twice.
        private async Task PublishOutcomeAsync(FolkIdleDbContext db, long playerId, EquipAttemptOutcome outcome)
        {
            if (outcome.ResultCode.HasValue)
            {
                _playerRegistry?.EnqueueCommandResult(playerId, outcome.ResultCode.Value);
            }

            if (outcome.Committed)
            {
                var update = outcome.Notification;
                try
                {
                    db.ChangeTracker.Clear();
                    update = await WithCharacterToolsAsync(db, update);
                }
                catch (Exception ex)
                {
                    // Unresolved tools are left alone by the tick (ToolsResolved
                    // false), so a failed read costs nothing but this line.
                    Console.WriteLine($"Equip tool loadout read failed for player {playerId}: {ex.Message}");
                }
                _playerRegistry?.EquipmentSlotUpdateQueue.Enqueue(update);
            }
        }

        // Locks the target character's row. Guid.Empty resolves to the main
        // character, which is the one whose Id equals PlayerRecords.PlayerGuid -
        // the same character StateCheckpointManager hydrates into slot 1.
        /// <summary>
        /// The character LoadPlayerState fields at <paramref name="rank"/> (0 =
        /// slot 1): non-escrowed, ordered by SlotIndex then Id. Guid.Empty when
        /// there is none.
        /// </summary>
        internal static async Task<Guid> FieldedCharacterIdAsync(FolkIdleDbContext db, long playerId, int rank)
        {
            return await db.CharacterRecords.AsNoTracking()
                .Where(c => c.PlayerId == playerId && !c.IsLockedInEscrow)
                .OrderBy(c => c.SlotIndex).ThenBy(c => c.Id)
                .Skip(rank)
                .Select(c => c.Id)
                .FirstOrDefaultAsync();
        }

        private static async Task<CharacterRecord?> ResolveCharacterForUpdateAsync(FolkIdleDbContext db, long playerId, Guid characterId)
        {
            if (characterId == Guid.Empty)
            {
                // Modul: "THE MAIN CHARACTER" IS WHOEVER STANDS IN SLOT 1
                // (2026-10-20). This was PlayerGuid - the founder - so the
                // loot list's Wear (wearOnMain, which sends no character)
                // dressed a founder who on every active account had been
                // benched: the piece vanished onto someone the Character
                // screen cannot show. Same order as LoadPlayerState fields
                // slot 1 by, and /player/worn falls back to the same one.
                characterId = await FieldedCharacterIdAsync(db, playerId, 0);
                if (characterId == Guid.Empty)
                {
                    return null;
                }
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

        internal static async Task<EquipmentSlotUpdateNotification> BuildNotificationAsync(FolkIdleDbContext db, CharacterRecord character)
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

        /// <summary>
        /// Modul: THE TOOL FIELDS HAD NO WRITER (found 2026-10-10). The tick
        /// copied all six onto the payload on every equip update and nothing
        /// filled them, so equipping or removing ANY piece set every tool tier
        /// and tool affix to zero until the next login. This fills them and
        /// marks the update ToolsResolved, which is what the tick asks before
        /// it copies them.
        ///
        /// Modul: THE CHARACTER'S OWN TOOLS, NOT THE FOUNDER'S (2026-10-20).
        /// This read the tools off PlayerGuid's character while login read them
        /// off slot 1 - two answers to one question - and on every active
        /// account the founder had been benched, so one equip swapped the
        /// gatherer's axe for a benched character's until the next login. The
        /// tools now belong to the character in the update, and the tick lands
        /// them on that character's own register (CharacterActivityState).
        ///
        /// CALLED AFTER THE EQUIP COMMITS, never inside it. The equip runs
        /// Serializable, and a read here inside the transaction was a read/
        /// write conflict that aborted equips (exercise.mjs caught it - a
        /// weapon taken off could not be put back on).
        /// </summary>
        internal static async Task<EquipmentSlotUpdateNotification> WithCharacterToolsAsync(FolkIdleDbContext db, EquipmentSlotUpdateNotification update)
        {
            var character = await db.CharacterRecords.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == update.CharacterId && c.PlayerId == update.PlayerId);
            ToolLoadout tools = character == null ? ToolLoadout.Empty : await ResolveToolLoadoutAsync(db, character);
            update.AxeToolTier = tools.AxeTier;
            update.PickaxeToolTier = tools.PickaxeTier;
            update.RodToolTier = tools.RodTier;
            update.ToolAxeSpeedPct = tools.AxeSpeedPct;
            update.ToolPickaxeSpeedPct = tools.PickaxeSpeedPct;
            update.ToolRodSpeedPct = tools.RodSpeedPct;
            update.ToolAxeYieldPct = tools.AxeYieldPct;
            update.ToolPickaxeYieldPct = tools.PickaxeYieldPct;
            update.ToolRodYieldPct = tools.RodYieldPct;
            update.ToolAxeRareFindPct = tools.AxeRareFindPct;
            update.ToolPickaxeRareFindPct = tools.PickaxeRareFindPct;
            update.ToolRodRareFindPct = tools.RodRareFindPct;
            update.ToolsResolved = true;
            return update;
        }

        /// <summary>One character's tool loadout - login's and the equip update's single answer.</summary>
        internal static async Task<ToolLoadout> ResolveToolLoadoutAsync(FolkIdleDbContext db, CharacterRecord character)
        {
            var ids = new List<long>(3);
            if (character.EquippedAxeId.HasValue) ids.Add(character.EquippedAxeId.Value);
            if (character.EquippedPickaxeId.HasValue) ids.Add(character.EquippedPickaxeId.Value);
            if (character.EquippedRodId.HasValue) ids.Add(character.EquippedRodId.Value);
            if (ids.Count == 0) return ToolLoadout.Empty;

            var rows = await db.EquipmentInstances.AsNoTracking()
                .Where(e => ids.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id);
            return ToolLoadoutResolver.Resolve(character, rows);
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
            var (totals, setIds) = await ComputeGearTotalsAsync(db, character);

            // Modul: the character's pet rides the same totals as its gear, so
            // every path that already reads them - live, offline, the world
            // boss, the guild-war snapshot - counts it without a second copy.
            // Task 114: up to two pets (the second slot opens at five rebirths).
            var petIds = await db.PlayerPets.AsNoTracking()
                .Where(p => p.CharacterId == character.Id)
                .Select(p => p.PetId)
                .ToListAsync();
            foreach (var petId in petIds)
            {
                if (FolkIdle.Server.Domain.Progression.PetRegistry.Find(petId) is { } pet)
                {
                    FolkIdle.Server.Domain.Progression.PetRegistry.AddTo(pet, ref totals);
                }
            }
            return (totals, setIds);
        }

        private static async Task<(EquippedAffixTotals Totals, EquippedSetIds SetIds)> ComputeGearTotalsAsync(FolkIdleDbContext db, CharacterRecord character)
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
                    // Modul: the set comes from the piece's BaseId (task 63), not
                    // from EquipmentInstance.SetId - nothing ever wrote that
                    // column, so no bonus had ever paid. See ArmourSetRegistry.
                    setIds.SetBySlotIndex(pieceSlotIndex, ArmourSetRegistry.SetIdOf(piece.BaseItemId), piece.QualityTier);
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
            => (await ComputeCharacterCombatStatsWithSetsAsync(db, player, character, humanMastery, vilaMastery, draugrMastery, completedAreaFlags)).Stats;

        // Task 63: the same computation, also handing back the worn set ids so
        // the Character screen can list what each set pays from the server's
        // own evaluation instead of a second copy of the catalogue.
        public static async Task<(CombatStats Stats, EquippedSetIds SetIds)> ComputeCharacterCombatStatsWithSetsAsync(
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
            TraitTotals traits = default;

            if (character.Lineage != null)
            {
                activeRaceId = (int)(character.Lineage.GeneticVector & 0xFF);
                isEpicMutation = character.Lineage.IsEpicMutation;
                traits = TraitTotals.From(character.Lineage.TraitMask);
            }

            (EquippedAffixTotals totals, EquippedSetIds setIds) = await ComputeEquippedTotalsAsync(db, character);

            return (StatsCalculator.Calculate(
                player.BaseStrength, player.BaseDexterity, player.BaseConstitution, player.BaseLuck,
                player.ActiveOffensivePotionId, player.ActiveDefensivePotionId,
                activeAgePhase, completedAreaFlags, activeRaceId,
                humanMastery, vilaMastery, draugrMastery,
                totals, isEpicMutation, traits, setIds), setIds);
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
        // Modul: public so BossGearBenchmark can fold a synthetic loadout's
        // affixes through the SAME mapping the equip path uses. A benchmark with
        // its own copy of this switch would drift from it, and the whole point
        // of projecting the boss ladder is that the projection agrees with the
        // tick.
        public static void AddAffixTotals(string affixPayload, ref EquippedAffixTotals totals)
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
                    case "boss_dmg_pct": totals.WorldBossDamageTenthsPct += magnitude; break;
                    case "loot_luck_pct": totals.LootLuckTenthsPct += magnitude; break;
                    case "gold_find_pct": totals.GoldTenthsPct += magnitude; break;
                }
            }
        }
    }
}
