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

        public static List<EquipmentInstance> Seed(FolkIdleDbContext db, long playerId)
        {
            var granted = new List<EquipmentInstance>(StarterToolBaseIds.Length);
            foreach (string baseId in StarterToolBaseIds)
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
