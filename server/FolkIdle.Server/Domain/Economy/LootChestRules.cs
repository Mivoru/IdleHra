using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>A loot chest's tier. The stage art maps 1-4 to common/rare/epic/legendary.</summary>
    public enum LootChestTier : byte
    {
        Bronze = 1,
        Silver = 2,
        Gold = 3,
        Ancient = 4,
    }

    /// <summary>One opened chest, before anything is written.</summary>
    public sealed record LootChestRoll(
        IReadOnlyList<(string ItemId, long Quantity)> Materials,
        long Gold,
        bool Equipment,
        int EquipmentRarityFloor,
        int Diamonds,
        long GoldInsteadOfDiamonds);

    /// <summary>
    /// What a loot chest holds (CHESTS_DUNGEONS_DIAMONDS.md section 1, owner
    /// decisions L1-L3, 2026-10-30). Pure: the caller passes the region the
    /// chest was earned in, the player's highest unlocked region, a gold
    /// income per minute and the room left under the weekly diamond ceiling,
    /// and gets back what to grant. The engine that writes it is separate.
    /// </summary>
    /// <remarks>
    /// Modul: EVERY AMOUNT IS TIME, NOT A FLAT NUMBER. Materials are minutes of
    /// the region's reference gathering (the Workshop's own price unit), gold
    /// is minutes of the player's own income. A flat number is worthless at
    /// the top or a fortune at the bottom - the Delve's gate made that
    /// mistake first.
    ///
    /// Modul: THE NUMBERS ARE A FIRST CUT, chosen on 2026-10-30 against what
    /// already pays the same thing (a Bronze chest is about one Delve run's
    /// materials, an Ancient about three hours of gathering) and meant to be
    /// re-measured once chests have been opened in production for a week.
    ///
    /// Modul: DIAMONDS SIT INSIDE A WEEKLY CEILING like every free tap. A roll
    /// that would pass it pays gold instead (the Delve's rule), so a chest is
    /// never worth nothing and never breaks the diamond economy.
    /// </remarks>
    public static class LootChestRules
    {
        public readonly record struct TierTable(
            int MaterialMinutes,
            int TopRegionMaterialMinutes,
            int GoldMinutes,
            int EquipmentPermille,
            int EquipmentRarityFloor,
            int DiamondPermille,
            int DiamondsMin,
            int DiamondsMax);

        private static readonly TierTable[] Tables =
        {
            default,
            new(MaterialMinutes: 20, TopRegionMaterialMinutes: 0, GoldMinutes: 10, EquipmentPermille: 150, EquipmentRarityFloor: RarityTier.Common, DiamondPermille: 0, DiamondsMin: 0, DiamondsMax: 0),
            new(MaterialMinutes: 45, TopRegionMaterialMinutes: 15, GoldMinutes: 25, EquipmentPermille: 350, EquipmentRarityFloor: RarityTier.Common, DiamondPermille: 30, DiamondsMin: 1, DiamondsMax: 3),
            new(MaterialMinutes: 90, TopRegionMaterialMinutes: 45, GoldMinutes: 60, EquipmentPermille: 700, EquipmentRarityFloor: RarityTier.Rare, DiamondPermille: 150, DiamondsMin: 2, DiamondsMax: 5),
            new(MaterialMinutes: 180, TopRegionMaterialMinutes: 120, GoldMinutes: 120, EquipmentPermille: 1000, EquipmentRarityFloor: RarityTier.Epic, DiamondPermille: 1000, DiamondsMin: 5, DiamondsMax: 10),
        };

        /// <summary>Gold paid for each diamond the weekly ceiling refused, in minutes of income.</summary>
        public const int GoldMinutesPerRefusedDiamond = 2;

        /// <summary>Share of a material line that is the region's rare log/ore, as the Workshop quotes it.</summary>
        private const double RareShare = 0.1;

        /// <summary>
        /// Herbs and potions (step 5, 2026-10-31): every chest carries herbs of
        /// its region, half as many minutes as its log/ore line. Priced at the
        /// owner's rate - 150 herbs is about ten minutes of picking - so a
        /// Bronze chest's ten minutes brew one potion, an Ancient's ninety
        /// about nine. The rare herb is the same tenth as the rare log and ore.
        /// </summary>
        public const int HerbsPerHourOfPicking = 900;

        public static int HerbMinutesFor(in TierTable t) => t.MaterialMinutes / 2;

        /// <summary>The region's herb pair (items 442-451, common then rare).</summary>
        public static (string Common, string Rare) HerbsOf(int region)
        {
            int r = Math.Clamp(region, WorkshopCommissionRules.FirstRegion, WorkshopCommissionRules.LastRegion);
            return (ContentRegistry.GetItemBaseId(442 + 2 * (r - 1)), ContentRegistry.GetItemBaseId(443 + 2 * (r - 1)));
        }

        public static TierTable TableFor(LootChestTier tier) => Tables[Math.Clamp((int)tier, 1, 4)];

        /// <summary>
        /// The published odds, one line per slot, for the chest screen (store
        /// rules ask for them, and the cosmetic chests already show theirs).
        /// </summary>
        public static IReadOnlyList<string> OddsLines(LootChestTier tier)
        {
            var t = TableFor(tier);
            var lines = new List<string> { $"Materials of the region: always ({t.MaterialMinutes} min of gathering)" };
            if (t.TopRegionMaterialMinutes > 0) lines.Add($"Materials of your highest region: always ({t.TopRegionMaterialMinutes} min)");
            lines.Add($"Herbs of the region: always ({HerbMinutesFor(in t)} min of picking)");
            lines.Add($"Gold: always ({t.GoldMinutes} min of your income)");
            lines.Add($"Equipment: {t.EquipmentPermille / 10.0:0.#}%, {RarityTier.GetName(t.EquipmentRarityFloor)} or better");
            if (t.DiamondPermille > 0) lines.Add($"Diamonds: {t.DiamondPermille / 10.0:0.#}%, {t.DiamondsMin}-{t.DiamondsMax}");
            return lines;
        }

        public static LootChestRoll Roll(
            LootChestTier tier, int chestRegion, int highestRegion, double goldPerMinute, int diamondRoomThisWeek, Random rng)
        {
            var t = TableFor(tier);
            int region = Math.Clamp(chestRegion, WorkshopCommissionRules.FirstRegion, WorkshopCommissionRules.LastRegion);
            int top = Math.Clamp(Math.Max(region, highestRegion), WorkshopCommissionRules.FirstRegion, WorkshopCommissionRules.LastRegion);

            var materials = new List<(string, long)>();
            AddMaterials(materials, region, t.MaterialMinutes);
            if (t.TopRegionMaterialMinutes > 0) AddMaterials(materials, top, t.TopRegionMaterialMinutes);
            AddHerbs(materials, region, HerbMinutesFor(in t));

            long gold = (long)Math.Round(Math.Max(0.0, goldPerMinute) * t.GoldMinutes);
            bool equipment = rng.Next(1000) < t.EquipmentPermille;

            int diamonds = 0;
            long goldInstead = 0;
            if (t.DiamondPermille > 0 && rng.Next(1000) < t.DiamondPermille)
            {
                int rolled = rng.Next(t.DiamondsMin, t.DiamondsMax + 1);
                diamonds = Math.Clamp(Math.Max(0, diamondRoomThisWeek), 0, rolled);
                goldInstead = (long)Math.Round(Math.Max(0.0, goldPerMinute) * GoldMinutesPerRefusedDiamond * (rolled - diamonds));
            }

            return new LootChestRoll(materials, gold, equipment, t.EquipmentRarityFloor, diamonds, goldInstead);
        }

        private static void AddMaterials(List<(string, long)> into, int region, int minutes)
        {
            var mats = VillageManagementEngine.GetTierMaterials((region - 1) * 5);
            double hours = minutes / 60.0;
            double wood = WorkshopCommissionRules.ReferenceUnitsPerHour(region, mining: false) * hours;
            double ore = WorkshopCommissionRules.ReferenceUnitsPerHour(region, mining: true) * hours;
            Merge(into, mats.Log, (long)Math.Round(wood * (1.0 - RareShare)));
            Merge(into, mats.Ore, (long)Math.Round(ore * (1.0 - RareShare)));
            Merge(into, mats.RareLog, Math.Max(1L, (long)Math.Round(wood * RareShare)));
            Merge(into, mats.RareOre, Math.Max(1L, (long)Math.Round(ore * RareShare)));
        }

        private static void AddHerbs(List<(string, long)> into, int region, int minutes)
        {
            if (minutes <= 0) return;
            var (common, rare) = HerbsOf(region);
            double herbs = HerbsPerHourOfPicking * (minutes / 60.0);
            Merge(into, common, (long)Math.Round(herbs * (1.0 - RareShare)));
            Merge(into, rare, Math.Max(1L, (long)Math.Round(herbs * RareShare)));
        }

        private static void Merge(List<(string ItemId, long Quantity)> into, string item, long quantity)
        {
            if (quantity <= 0) return;
            for (int i = 0; i < into.Count; i++)
            {
                if (into[i].ItemId == item)
                {
                    into[i] = (item, into[i].Quantity + quantity);
                    return;
                }
            }
            into.Add((item, quantity));
        }
    }
}
