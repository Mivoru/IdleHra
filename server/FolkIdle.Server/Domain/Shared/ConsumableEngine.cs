using System;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Shared
{
    // Modul: Deferred Part 5 Implementation, Part 2. Foods, potions, and
    // the Death Ward Elixir. Consumable identity stays in the existing
    // unmanaged int item-id space (TickStatePayload.ActiveOffensivePotionId/
    // ActiveDefensivePotionId plus the new ActiveFoodBuffId) - the task's
    // varchar ids would put string comparisons on the 10Hz tick and
    // violate its own zero-allocation constraint, so ids are classified
    // by BaseId marker exactly once at consumption time (command-time,
    // not tick-time), and every per-tick check below is pure integer
    // arithmetic over payload fields.
    //
    // Expiry model: the payload carries millisecond countdowns decremented
    // by TickBuffCountdowns on every 10Hz tick (the pre-existing potion
    // idiom); StateCheckpointManager converts the remaining countdown to
    // an absolute server-epoch expiry on flush and back on load, giving
    // the durable "ExpiresEpoch" persistence the task asks for without
    // trusting any client clock.
    public static class ConsumableEngine
    {
        // Five minutes: the legacy potions' length, kept for the Death Ward tests.
        // Herb potions carry their own (PotionRegistry).
        public const int PotionDurationMs = 300000;
        public const int FoodDurationMs = 600000;

        // Death Ward revive fraction: 20 percent of effective max HP.
        public const int DeathWardReviveDivisor = 5;

        // Food regen: effectiveMaxHp / FoodRegenDivisor milli-HP per tick
        // (10Hz), i.e. 2 percent of max HP per second.
        public const int FoodRegenDivisor = 500;

        // Modul: NO CONTENT FEEDS THIS ENGINE, 2026-10-07. The last five
        // consumables (roasted_perch, viper_stew, bear_stew, yeti_platter
        // and the Death Ward Elixir, ids 372-375 and 379) were deleted by
        // owner decision: no recipe, drop, shop or reward ever produced one,
        // and production held none. The Boosts tab went with them. The
        // engine itself was kept on purpose - removing it reaches into the
        // combat tick, the offline projection, the wire and PlayerRecords -
        // so DeathWardItemId resolves to 0 and every path below is inert.
        // A new consumable only needs an items.json entry with the BaseId
        // marker; the tests arm the ward with UseDeathWardItemIdForTests.
        private static int _deathWardItemId = -1;

        // Test-only: content holds no Death Ward, so tests point the ward at
        // an id no item uses. Never called by the server.
        internal static void UseDeathWardItemIdForTests(int itemId) => _deathWardItemId = itemId;

        // Resolved once (first access after ContentRegistry.Initialize),
        // then a plain int compare on every subsequent call - the lethal
        // interception check on the combat tick never touches a string.
        public static int DeathWardItemId
        {
            get
            {
                if (_deathWardItemId < 0)
                {
                    _deathWardItemId = ContentRegistry.TryGetItemDefinitionByBaseId("death_ward_elixir_defensive_potion_consumable", out var definition)
                        ? definition.Id
                        : 0;
                }
                return _deathWardItemId;
            }
        }

        // Classifies and applies a consumable by its item id. Runs at
        // command time (ConsumeConsumableAsset), never on the tick -
        // string marker checks are permitted here. Returns false for item
        // ids that are not consumables (the caller falls through to the
        // legacy status-effect path unchanged).
        public static bool TryApplyConsumable(ref TickStatePayload payload, int itemId)
        {
            if (itemId <= 0 || itemId > ContentRegistry.ItemDefinitions.Length)
            {
                return false;
            }

            string baseId = ContentRegistry.GetItemBaseId(itemId);

            if (baseId.Contains("_food_consumable"))
            {
                payload.ActiveFoodBuffId = itemId;
                payload.FoodBuffDurationMs = FoodDurationMs;
                payload.IsDirty = true;
                return true;
            }

            // Modul: POTIONS DO NOT COME THROUGH HERE (2026-10-31). This set the
            // buff and took nothing from the inventory - the consume command's
            // async half, which would remove a row, never runs once this
            // returns true, and it reads EquipmentInstances where a potion
            // stack never lives. With the herb potions in the catalogue that
            // was an unlimited free buff. A potion is drunk from its shelf
            // (TryDrinkFromShelf), which is the inventory it was moved into.
            return false;
        }

        /// <summary>
        /// Drinks the next potion on a slot's shelf: the slot takes its effect
        /// for the potion's full duration (a running potion is replaced, its
        /// remainder lost) and the shelf loses one. False when the shelf is
        /// empty or holds no potion of that slot.
        /// </summary>
        public static bool TryDrinkFromShelf(ref TickStatePayload payload, int slot)
        {
            ref int shelfItem = ref ShelfItem(ref payload, slot);
            ref int shelfCount = ref ShelfCount(ref payload, slot);
            if (shelfCount <= 0 || !PotionRegistry.TryGet(shelfItem, out var potion) || potion.Slot != slot) return false;

            shelfCount--;
            payload.LifetimePotionsDrunk++;
            ActiveId(ref payload, slot) = potion.ItemId;
            Duration(ref payload, slot) = potion.DurationMs;
            if (shelfCount == 0) shelfItem = 0;
            payload.IsDirty = true;
            return true;
        }

        /// <summary>Whether auto-drink is on for a slot (bit N of PotionAutoDrinkMask).</summary>
        public static bool AutoDrinks(in TickStatePayload payload, int slot) => (payload.PotionAutoDrinkMask & (1 << slot)) != 0;

        internal static ref int ActiveId(ref TickStatePayload payload, int slot)
        {
            if (slot == PotionSlot.Rewards) return ref payload.ActiveOffensivePotionId;
            if (slot == PotionSlot.Combat) return ref payload.ActiveDefensivePotionId;
            return ref payload.ActiveGatheringPotionId;
        }

        internal static ref int Duration(ref TickStatePayload payload, int slot)
        {
            if (slot == PotionSlot.Rewards) return ref payload.OffensivePotionDurationMs;
            if (slot == PotionSlot.Combat) return ref payload.DefensivePotionDurationMs;
            return ref payload.GatheringPotionDurationMs;
        }

        internal static ref int ShelfItem(ref TickStatePayload payload, int slot)
        {
            if (slot == PotionSlot.Rewards) return ref payload.PotionShelf1_ItemId;
            if (slot == PotionSlot.Combat) return ref payload.PotionShelf2_ItemId;
            return ref payload.PotionShelf3_ItemId;
        }

        internal static ref int ShelfCount(ref TickStatePayload payload, int slot)
        {
            if (slot == PotionSlot.Rewards) return ref payload.PotionShelf1_Count;
            if (slot == PotionSlot.Combat) return ref payload.PotionShelf2_Count;
            return ref payload.PotionShelf3_Count;
        }

        // The 10Hz countdown for all three buff slots - extracted from
        // SimulationEngine's inline potion block so the expiry semantics
        // are directly unit-testable. Pure integer field writes, zero
        // allocation; expired slots clear to 0 without any string work.
        public static void TickBuffCountdowns(ref TickStatePayload payload)
        {
            // The three potion slots: count down, and when one runs out, the
            // next from its shelf if auto-drink is on - in the same tick, so an
            // auto-drunk potion never leaves a gap.
            for (int slot = 0; slot < PotionSlot.Count; slot++)
            {
                ref int duration = ref Duration(ref payload, slot);
                if (duration <= 0) continue;
                duration -= 100;
                if (duration > 0) continue;
                duration = 0;
                ActiveId(ref payload, slot) = 0;
                if (AutoDrinks(in payload, slot)) TryDrinkFromShelf(ref payload, slot);
                payload.IsDirty = true;
            }

            if (payload.FoodBuffDurationMs > 0)
            {
                payload.FoodBuffDurationMs -= 100;
                if (payload.FoodBuffDurationMs <= 0)
                {
                    payload.FoodBuffDurationMs = 0;
                    payload.ActiveFoodBuffId = 0;
                }
            }
        }

        // Death Ward Elixir lethal interception - called from the combat
        // loop's death branch BEFORE the respawn reset. If the ward
        // occupies the defensive potion slot, the fatal blow is negated,
        // the player revives at exactly 20 percent of effective max HP,
        // and the ward clears its own active effect (one charge). Pure
        // integer compare and writes - zero allocation on the tick.
        public static bool TryInterceptLethalDamage(ref TickStatePayload payload, int effectiveMaxHp)
        {
            int wardId = DeathWardItemId;
            if (wardId <= 0 || payload.ActiveDefensivePotionId != wardId)
            {
                return false;
            }

            payload.PlayerHp = effectiveMaxHp / DeathWardReviveDivisor;
            if (payload.PlayerHp < 1)
            {
                payload.PlayerHp = 1;
            }
            payload.ActiveDefensivePotionId = 0;
            payload.DefensivePotionDurationMs = 0;
            payload.IsDirty = true;
            return true;
        }
    }
}
