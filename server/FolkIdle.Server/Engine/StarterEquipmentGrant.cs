using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FolkIdle.Server.Domain.Combat;
using System.Text.Json;
using FolkIdle.Server.Models;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// What a brand-new account owns before it has done anything.
    ///
    /// Three tools, one per gathering profession. Gathering resolves the tool
    /// that matches the job - an axe for wood, a pickaxe for ore, a rod for
    /// fish - so an account holding none of them could not usefully do any of
    /// the three professions the game opens on, and the only route to a tool is
    /// crafting one out of materials those professions produce.
    ///
    /// These are the catalogue's own `normal_*_tool` entries at tier 0: the
    /// weakest tools that exist, worth nothing on the market, and superseded by
    /// the first Birch set a player crafts. They are a starting position, not a
    /// gift.
    ///
    /// Both registration paths call this - device auto-provisioning and
    /// ordinary sign-up - because seeding one and not the other is how the
    /// "some accounts start with 25 copper ore" report happened in the first
    /// place.
    /// </summary>
    public static class StarterEquipmentGrant
    {
        public static readonly string[] StarterToolBaseIds =
        {
            "normal_axe_tool",
            "normal_pickaxe_tool",
            "normal_fishing_rod_tool",
        };

        /// <summary>
        /// Modul: A WEAPON AND A LITTLE FOOD, 2026-09-28 - the owner's call,
        /// reversing the 2026-09-23 "no starter weapon". A new player used to
        /// open on "go fishing, or the first monster kills you", naked. Now the
        /// chest holds a Normal region-1 claymore (region 1 asks no attribute,
        /// see EquipmentAttributeGate) and a few fish, and the tutorial walks
        /// the player through wearing one and loading the other - by hand,
        /// because doing it is how they learn where those two things live.
        /// Both go in the CHEST, not on the character, for exactly that reason.
        ///
        /// Ten fish, not a larder's worth: enough to win the first fights, few
        /// enough that "fish, then load the larder" is still the lesson a few
        /// minutes later.
        /// </summary>
        public const string StarterWeaponBaseId = "eq_steel_claymore_melee_weapon_slot_base";
        public const int StarterFishCount = 10;

        public static List<EquipmentInstance> Seed(FolkIdleDbContext db, long playerId)
        {
            var granted = new List<EquipmentInstance>(StarterToolBaseIds.Length + 1);
            foreach (string baseId in StarterToolBaseIds.Append(StarterWeaponBaseId))
            {
                var rolled = new Dictionary<string, int>();
                AffixRegistry.RollAffixes(
                    baseId,
                    regionTier: 1,
                    itemRarityTier: RarityTier.Normal,
                    affixCount: RarityTier.GetAffixCount(RarityTier.Normal),
                    destination: rolled);

                var instance = new EquipmentInstance
                {
                    BaseItemId = baseId,
                    PlayerId = playerId,
                    QualityTier = RarityTier.Normal,
                    AffixPayload = JsonSerializer.Serialize(rolled),
                    IsAffixLocked = false,
                };
                db.EquipmentInstances.Add(instance);
                granted.Add(instance);
            }
            return granted;
        }

        /// <summary>
        /// Puts the starter tools on the account's first character. Call after
        /// the instances have been saved, so they have ids.
        /// </summary>
        /// <remarks>
        /// Modul: WORN, NOT PACKED. The tools used to be granted into the chest
        /// and left there, so a brand-new Character screen showed eleven empty
        /// slots and Gathering read "axe 0 - pickaxe 0 - rod 0" to a player
        /// holding all three - the first thing they saw looked like a missing
        /// grant. Slots 8, 9 and 10 (CLAUDE.md "ELEVEN equipment slots").
        /// </remarks>
        /// <summary>The starter fish, into the chest. See StarterFishCount.</summary>
        public static async Task SeedStarterFoodAsync(FolkIdleDbContext db, long playerId)
        {
            int fishId = FoodRegistry.FirstRawFishOfTier(1);
            if (fishId <= 0) return;
            string fishBaseId = ContentRegistry.GetItemBaseId(fishId);
            if (string.IsNullOrEmpty(fishBaseId)) return;
            await CommodityLedger.AddAsync(db, playerId, fishBaseId, StarterFishCount);
        }

        public static async Task EquipOnAsync(FolkIdleDbContext db, Guid characterId, IReadOnlyList<EquipmentInstance> tools)
        {
            var character = db.CharacterRecords.Local.FirstOrDefault(c => c.Id == characterId)
                ?? await db.CharacterRecords.FindAsync(characterId);
            if (character == null) return;

            foreach (var tool in tools)
            {
                int slot = EquipmentSlotEngine.ResolveSlotIndex(tool.BaseItemId);
                if (slot < EquipmentSlotEngine.SlotAxe || slot > EquipmentSlotEngine.SlotRod) continue;
                EquipmentSlotEngine.WriteSlot(character, slot, tool.Id);
            }
        }
    }
}
