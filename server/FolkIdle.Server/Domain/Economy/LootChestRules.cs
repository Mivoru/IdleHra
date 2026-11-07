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
        int EquipmentTier,
        int Diamonds,
        long GoldInsteadOfDiamonds,
        int DungeonTickets = 0);

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
            new(MaterialMinutes: 20, TopRegionMaterialMinutes: 0, GoldMinutes: 10, EquipmentPermille: 500, EquipmentRarityFloor: RarityTier.Epic, DiamondPermille: 0, DiamondsMin: 0, DiamondsMax: 0),
            new(MaterialMinutes: 45, TopRegionMaterialMinutes: 15, GoldMinutes: 25, EquipmentPermille: 1000, EquipmentRarityFloor: RarityTier.Legendary, DiamondPermille: 30, DiamondsMin: 1, DiamondsMax: 3),
            new(MaterialMinutes: 90, TopRegionMaterialMinutes: 45, GoldMinutes: 60, EquipmentPermille: 1000, EquipmentRarityFloor: RarityTier.Mythic, DiamondPermille: 150, DiamondsMin: 2, DiamondsMax: 5),
            new(MaterialMinutes: 180, TopRegionMaterialMinutes: 120, GoldMinutes: 120, EquipmentPermille: 1000, EquipmentRarityFloor: RarityTier.Relic, DiamondPermille: 1000, DiamondsMin: 5, DiamondsMax: 10),
        };

        /// <summary>Parts in which <see cref="RarityOdds"/> is written: ten million.</summary>
        public const int RarityOddsScale = 10_000_000;

        /// <summary>
        /// The rarity of a chest's equipment piece, per chest tier, in parts of
        /// ten million, indexed by quality tier 1-14 (index 0 unused). Each row
        /// sums to <see cref="RarityOddsScale"/>; nothing falls below the floor.
        /// </summary>
        /// <remarks>
        /// Modul: A CHEST PIECE HAS TO BEAT WHAT KILLS ALREADY PAY (owner,
        /// 2026-11-07: opening chests "for rare/epic/legendary equipment is not
        /// worth it"). Measured on a 24 game hour run of 20 bots, kills drop
        /// about 5,000 pieces a player a day: ~69 Epic, 29 Legendary, 14
        /// Mythic, 3 Relic and 1 Ancient, a Godly every ~39 days and a
        /// Transcendent every ~390. The old chest rolled the KILL ladder above
        /// a floor (an Ancient chest was an Epic 99% of the time), so its piece
        /// was worth nothing. These rows start where an hour (Bronze) to
        /// several days (Ancient) of kills ends, and the owner raised every
        /// tier above the floor by half again over the first proposal. The
        /// power cost is small: the whole rarity ladder is worth x2.12, one
        /// region step x3, and chests are few.
        /// </remarks>
        private static readonly int[][] RarityOdds =
        {
            System.Array.Empty<int>(),
            //      -  Nor Com Unc Rar UR  Epic       Legendary  Mythic     Relic      Ancient    Divine   Demonic  Godly   Transcendent
            new[] { 0, 0, 0, 0, 0, 0, 5_500_000, 3_000_000, 1_200_000,   240_000,    45_000,  12_000,   2_550,    375,     75 }, // Bronze
            new[] { 0, 0, 0, 0, 0, 0,         0, 5_500_000, 3_600_000,   705_000,   150_000,  36_000,   7_500,  1_350,    150 }, // Silver
            new[] { 0, 0, 0, 0, 0, 0,         0,         0, 6_100_000, 3_000_000,   675_000, 165_000,  45_000, 12_000,  3_000 }, // Gold
            new[] { 0, 0, 0, 0, 0, 0,         0,         0,         0, 6_100_000, 3_000_000, 600_000, 210_000, 75_000, 15_000 }, // Ancient
        };

        /// <summary>A quality tier's chance in a chest tier's piece, out of <see cref="RarityOddsScale"/>.</summary>
        public static int RarityOddsFor(LootChestTier tier, int qualityTier)
        {
            var row = RarityOdds[Math.Clamp((int)tier, 1, 4)];
            return qualityTier >= 1 && qualityTier < row.Length ? row[qualityTier] : 0;
        }

        /// <summary>Rolls the quality tier of a chest's equipment piece.</summary>
        public static int RollEquipmentTier(LootChestTier tier, Random rng)
        {
            var row = RarityOdds[Math.Clamp((int)tier, 1, 4)];
            int roll = rng.Next(RarityOddsScale);
            for (int q = 1; q < row.Length; q++)
            {
                roll -= row[q];
                if (roll < 0) return q;
            }
            return TableFor(tier).EquipmentRarityFloor;
        }

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
        /// <summary>
        /// The chance a chest holds one dungeon ticket for a random gate
        /// (owner, 2026-10-31): Bronze 10%, Silver 20%, Gold 35%, Ancient 60%.
        /// Chests are few - the weekly Deep, first clears, Ascension steps and
        /// won Equipment runs - so the loop dungeon -> chest -> ticket pays
        /// about 0.2-0.7 tickets a day, never a run for free.
        /// </summary>
        public static int DungeonTicketPermille(LootChestTier tier) => tier switch
        {
            LootChestTier.Bronze => 100,
            LootChestTier.Silver => 200,
            LootChestTier.Gold => 350,
            _ => 600,
        };

        public static IReadOnlyList<string> OddsLines(LootChestTier tier, bool dungeonTickets = false)
        {
            var t = TableFor(tier);
            var lines = new List<string> { $"Materials of the region: always ({t.MaterialMinutes} min of gathering)" };
            if (t.TopRegionMaterialMinutes > 0) lines.Add($"Materials of your highest region: always ({t.TopRegionMaterialMinutes} min)");
            lines.Add($"Herbs of the region: always ({HerbMinutesFor(in t)} min of picking)");
            lines.Add($"Gold: always ({t.GoldMinutes} min of your income)");
            lines.Add($"Equipment: {t.EquipmentPermille / 10.0:0.#}%, {RarityTier.GetName(t.EquipmentRarityFloor)} or better, from your highest region");
            var odds = new List<string>();
            for (int q = t.EquipmentRarityFloor; q <= RarityTier.Transcendent; q++)
            {
                double pct = RarityOddsFor(tier, q) * 100.0 / RarityOddsScale;
                odds.Add($"{RarityTier.GetName(q)} {pct.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}%");
            }
            lines.Add("Equipment rarity: " + string.Join(", ", odds));
            if (t.DiamondPermille > 0) lines.Add($"Diamonds: {t.DiamondPermille / 10.0:0.#}%, {t.DiamondsMin}-{t.DiamondsMax}");
            if (dungeonTickets) lines.Add($"A dungeon ticket: {DungeonTicketPermille(tier) / 10.0:0.#}%");
            return lines;
        }

        public static LootChestRoll Roll(
            LootChestTier tier, int chestRegion, int highestRegion, double goldPerMinute, int diamondRoomThisWeek, Random rng,
            bool dungeonTickets = false)
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
            int equipmentTier = equipment ? RollEquipmentTier(tier, rng) : 0;

            int diamonds = 0;
            long goldInstead = 0;
            if (t.DiamondPermille > 0 && rng.Next(1000) < t.DiamondPermille)
            {
                int rolled = rng.Next(t.DiamondsMin, t.DiamondsMax + 1);
                diamonds = Math.Clamp(Math.Max(0, diamondRoomThisWeek), 0, rolled);
                goldInstead = (long)Math.Round(Math.Max(0.0, goldPerMinute) * GoldMinutesPerRefusedDiamond * (rolled - diamonds));
            }

            // Rolled last, so every earlier draw is what it was before tickets existed.
            int tickets = dungeonTickets && rng.Next(1000) < DungeonTicketPermille(tier) ? 1 : 0;
            return new LootChestRoll(materials, gold, equipment, equipmentTier, diamonds, goldInstead, tickets);
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
