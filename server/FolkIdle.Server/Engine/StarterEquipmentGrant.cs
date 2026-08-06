using System.Collections.Generic;
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

        public static void Seed(FolkIdleDbContext db, long playerId)
        {
            foreach (string baseId in StarterToolBaseIds)
            {
                var rolled = new Dictionary<string, int>();
                AffixRegistry.RollAffixes(
                    baseId,
                    regionTier: 1,
                    itemRarityTier: RarityTier.Normal,
                    affixCount: RarityTier.GetAffixCount(RarityTier.Normal),
                    destination: rolled);

                db.EquipmentInstances.Add(new EquipmentInstance
                {
                    BaseItemId = baseId,
                    PlayerId = playerId,
                    QualityTier = RarityTier.Normal,
                    AffixPayload = JsonSerializer.Serialize(rolled),
                    IsAffixLocked = false,
                });
            }
        }
    }
}
