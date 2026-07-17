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
        public const int PotionDurationMs = 300000;
        public const int FoodDurationMs = 600000;

        // Death Ward revive fraction: 20 percent of effective max HP.
        public const int DeathWardReviveDivisor = 5;

        // Food regen: effectiveMaxHp / FoodRegenDivisor milli-HP per tick
        // (10Hz), i.e. 2 percent of max HP per second.
        public const int FoodRegenDivisor = 500;

        private static int _deathWardItemId = -1;

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

            if (baseId.Contains("_offensive_potion_consumable"))
            {
                payload.ActiveOffensivePotionId = itemId;
                payload.OffensivePotionDurationMs = PotionDurationMs;
                payload.IsDirty = true;
                return true;
            }

            if (baseId.Contains("_defensive_potion_consumable"))
            {
                payload.ActiveDefensivePotionId = itemId;
                payload.DefensivePotionDurationMs = PotionDurationMs;
                payload.IsDirty = true;
                return true;
            }

            return false;
        }

        // The 10Hz countdown for all three buff slots - extracted from
        // SimulationEngine's inline potion block so the expiry semantics
        // are directly unit-testable. Pure integer field writes, zero
        // allocation; expired slots clear to 0 without any string work.
        public static void TickBuffCountdowns(ref TickStatePayload payload)
        {
            if (payload.OffensivePotionDurationMs > 0)
            {
                payload.OffensivePotionDurationMs -= 100;
                if (payload.OffensivePotionDurationMs <= 0)
                {
                    payload.OffensivePotionDurationMs = 0;
                    payload.ActiveOffensivePotionId = 0;
                }
            }

            if (payload.DefensivePotionDurationMs > 0)
            {
                payload.DefensivePotionDurationMs -= 100;
                if (payload.DefensivePotionDurationMs <= 0)
                {
                    payload.DefensivePotionDurationMs = 0;
                    payload.ActiveDefensivePotionId = 0;
                }
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
