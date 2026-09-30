using System;
using System.Collections.Generic;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Economy
{
    /// <summary>
    /// Task 83: Workshop commissions - the material sink. Every number a
    /// commission has, and nothing that touches the database. The engine
    /// (<see cref="WorkshopCommissionEngine"/>), the REST view and the tests
    /// all ask here, so the price a player is quoted and the price the handler
    /// charges cannot drift apart - the Village screen's hand-kept price copy
    /// is the precedent (VillageManagementEngine.QuoteUpgrade).
    ///
    /// A commission makes ONE region piece (any of the 75 canonical drops of a
    /// region the player has opened), at a rarity FLOOR set by the Crafting
    /// Workshop's level, carrying ONE affix the player chose at Common affix
    /// rarity. It takes real time (1-8 h) and costs, in the piece's own
    /// region's wood and ore, what two characters gathering that region
    /// produce in that time. Spec: docs/superpowers/specs/2026-09-30-workshop-commissions.md.
    /// </summary>
    public static class WorkshopCommissionRules
    {
        /// <summary>
        /// Floor by Workshop level 1-5 (owner decision 2026-09-30: T2-T6).
        /// Index 0 is an unbuilt Workshop, which commissions nothing.
        /// </summary>
        private static readonly int[] _floorByWorkshopLevel =
        {
            0,
            RarityTier.Common,     // 2
            RarityTier.Uncommon,   // 3
            RarityTier.Rare,       // 4
            RarityTier.UltraRare,  // 5
            RarityTier.Epic,       // 6
        };

        /// <summary>
        /// How far below a region's usual gear a commission's floor must stay.
        /// </summary>
        /// <remarks>
        /// Modul: THE REGION'S USUAL DROP IS THE TIER ITS BOSS WALL ASSUMES.
        ///
        /// The owner's rule is that a commission's floor sits "below the
        /// region's usual drop". The median SINGLE drop cannot be the anchor:
        /// it is Normal at every region (51% of rolls), so every floor in the
        /// T2-T6 range would already be above it. A player wears the best of
        /// many drops, and the game has written down what that best is per
        /// region: BossFirstClearRules.RequiredQualityTierFor, the tier all
        /// eight slots of the region's own gear must reach to beat its boss
        /// (4, 8, 8, 10, 11). That is the region's usual equipped drop, stated
        /// in code and measured by BossWallTests.
        ///
        /// TWO tiers below it, not one, because BossWallTests asserts that
        /// "two tiers below the requirement loses" - so a wardrobe made
        /// entirely of commissions can never open a region the player could
        /// not otherwise open, and time-to-region-5 cannot move.
        /// PowerCeilingTests re-measures that directly. Only region 1 is ever
        /// capped by this (4 - 2 = Common); regions 2-5 allow the whole T6.
        /// </remarks>
        public const int TiersBelowRegionRequirement = 2;

        /// <summary>The first region and the last one a commission may make a piece of.</summary>
        public const int FirstRegion = 1;
        public const int LastRegion = 5;

        /// <summary>One commission at a time - the one PlayerCraftingSlots row, SlotIndex 0.</summary>
        public const byte CommissionSlotIndex = 0;

        /// <summary>The Workshop floor at this building level, ignoring the region: 0 when the Workshop is unbuilt.</summary>
        public static int WorkshopFloor(int workshopLevel)
        {
            if (workshopLevel <= 0) return 0;
            if (workshopLevel >= _floorByWorkshopLevel.Length) return _floorByWorkshopLevel[^1];
            return _floorByWorkshopLevel[workshopLevel];
        }

        /// <summary>The highest floor a commission of this region's pieces may carry.</summary>
        public static int RegionCeiling(int region)
            => Math.Max(RarityTier.Normal, BossFirstClearRules.RequiredQualityTierFor(region) - TiersBelowRegionRequirement);

        /// <summary>
        /// The rarity floor of a commission of a piece from <paramref name="region"/>
        /// at this Workshop level; 0 means the Workshop cannot take it.
        /// </summary>
        public static int FloorTierFor(int workshopLevel, int region)
        {
            int workshop = WorkshopFloor(workshopLevel);
            if (workshop <= 0) return 0;
            return Math.Min(workshop, RegionCeiling(region));
        }

        /// <summary>
        /// Real time, by floor: 1 h at Common rising to 8 h at Epic (the owner's
        /// 1-8 h). Longer for a better floor, because the floor is what the
        /// player is buying.
        /// </summary>
        public static long DurationSecondsFor(int floorTier) => floorTier switch
        {
            <= RarityTier.Common => 1L * 3600L,
            RarityTier.Uncommon => 2L * 3600L,
            RarityTier.Rare => 4L * 3600L,
            RarityTier.UltraRare => 6L * 3600L,
            _ => 8L * 3600L,
        };

        /// <summary>
        /// A reference character gathering one region's nodes: mastery, tool
        /// tier, village production level and codex yield.
        /// </summary>
        /// <remarks>
        /// Modul: PRICES COME FROM THE GATHERING MEASUREMENT, NOT A GUESS.
        /// Regions 1, 3 and 5 are GatheringEconomyTests' own profiles ("region
        /// 1, first axe", "region 3, keeping up", "region 5, geared"); regions 2
        /// and 4 sit between their neighbours. The units an hour are computed
        /// by GatheringToolEngine.ComputeRequiredTicks against the node's
        /// authored BaseTickThreshold - the same function the live tick and
        /// that test both use - so a retune of gathering speed re-prices
        /// commissions with it instead of leaving them stranded.
        /// </remarks>
        public readonly record struct ReferenceGatherer(int Mastery, int ToolTier, int VillageLevel, float CodexYield);

        private static readonly ReferenceGatherer[] _referenceGatherers =
        {
            new(5, 1, 1, 1.05f),
            new(20, 3, 3, 1.25f),
            new(40, 5, 5, 1.5f),
            new(60, 6, 6, 1.75f),
            new(80, 7, 8, 2.0f),
        };

        public static ReferenceGatherer ReferenceGathererFor(int region)
            => _referenceGatherers[Math.Clamp(region, FirstRegion, LastRegion) - FirstRegion];

        /// <summary>The share of a harvest that is the node's common material (GatheringEconomyTests: 90/10).</summary>
        public const double CommonShare = 0.9;

        private const int WoodcuttingNodeBase = 1000;
        private const int MiningNodeBase = 2000;
        private const int FallbackBaseTicks = 30;
        private const double TicksPerSecond = 10.0;

        /// <summary>Units of one node's harvest an hour for the region's reference gatherer (common + rare).</summary>
        public static double ReferenceUnitsPerHour(int region, bool mining)
        {
            int clamped = Math.Clamp(region, FirstRegion, LastRegion);
            int baseTicks = ContentRegistry.TryGetGatheringNode((mining ? MiningNodeBase : WoodcuttingNodeBase) + clamped, out var node)
                ? node.BaseTickThreshold
                : FallbackBaseTicks;
            var g = ReferenceGathererFor(clamped);
            int ticks = GatheringToolEngine.ComputeRequiredTicks(baseTicks, g.Mastery, g.ToolTier, g.VillageLevel);
            return 3600.0 / (ticks / TicksPerSecond) * g.CodexYield;
        }

        /// <summary>Round a price up to a readable figure: to 500 for commons, 50 for rares.</summary>
        private static long RoundUp(double value, long step)
            => Math.Max(step, (long)Math.Ceiling(value / step) * step);

        /// <summary>
        /// Everything a commission of a piece from <paramref name="region"/> at
        /// <paramref name="floorTier"/> costs, in the order it is charged.
        /// </summary>
        /// <remarks>
        /// Modul: TWO CHARACTERS' GATHERING FOR THE COMMISSION'S OWN DURATION.
        /// Each line is exactly what the region's reference gatherer yields of
        /// that material in the hours the commission takes: the common log and
        /// the common ore at 90% of a harvest, the golden log and the rare ore
        /// at 10%. So a commission eats what one woodcutter and one miner in
        /// its region produce while it is being made - a region-5 Epic is
        /// about 144,000 units, a region-2 Epic about 72,000, the region-1
        /// Common 6,600 - the "tens of thousands" the owner asked for,
        /// scaled to the region instead of fixed, because a fixed price is
        /// decoration at region 5 and a wall at region 1.
        ///
        /// The materials are the piece's OWN region's (VillageManagementEngine
        /// .GetTierMaterials, one pair per region - the ore canon). They are
        /// catalogued items, spent through the unified backpack+stash path.
        /// </remarks>
        public static VillageManagementEngine.UpgradeCostLine[] Quote(int region, int floorTier)
        {
            int clamped = Math.Clamp(region, FirstRegion, LastRegion);
            double hours = DurationSecondsFor(floorTier) / 3600.0;
            var mats = VillageManagementEngine.GetTierMaterials((clamped - 1) * 5);

            double wood = ReferenceUnitsPerHour(clamped, mining: false) * hours;
            double ore = ReferenceUnitsPerHour(clamped, mining: true) * hours;

            return new[]
            {
                new VillageManagementEngine.UpgradeCostLine(mats.Log, RoundUp(wood * CommonShare, 500)),
                new VillageManagementEngine.UpgradeCostLine(mats.Ore, RoundUp(ore * CommonShare, 500)),
                new VillageManagementEngine.UpgradeCostLine(mats.RareLog, RoundUp(wood * (1.0 - CommonShare), 50)),
                new VillageManagementEngine.UpgradeCostLine(mats.RareOre, RoundUp(ore * (1.0 - CommonShare), 50)),
            };
        }

        /// <summary>
        /// Whether <paramref name="itemId"/> is a piece a commission can make:
        /// droppable combat equipment (never a tool - tools are the recipe
        /// tree's) authored in one of the five regions.
        /// </summary>
        public static bool TryGetCommissionable(int itemId, out string baseItemId, out int region)
        {
            baseItemId = string.Empty;
            region = 0;
            ReadOnlySpan<ItemDefinition> items = ContentRegistry.ItemDefinitions;
            if (itemId < 1 || itemId > items.Length) return false;

            string baseId = ContentRegistry.GetItemBaseId(itemId);
            if (!EquipmentDropTable.IsDroppableEquipment(baseId)) return false;

            int authored = items[itemId - 1].RegionTier;
            if (authored < FirstRegion || authored > LastRegion) return false;

            baseItemId = baseId;
            region = authored;
            return true;
        }

        /// <summary>Every commissionable item id, in catalogue order.</summary>
        public static List<int> CommissionableItemIds()
        {
            var ids = new List<int>();
            int count = ContentRegistry.ItemDefinitions.Length;
            for (int id = 1; id <= count; id++)
            {
                if (TryGetCommissionable(id, out _, out _)) ids.Add(id);
            }
            return ids;
        }

        /// <summary>
        /// The affix ids a player may choose for this piece - exactly the ones
        /// its slot may roll (weapon-only damage on weapons, block on rings,
        /// never a tool affix). Fusion's legacy "_xxxx" keys are not ids and
        /// never appear here.
        /// </summary>
        public static List<string> LegalAffixIds(string baseItemId)
        {
            var result = new List<string>();
            var slot = AffixRegistry.ResolveSlot(baseItemId);
            if (slot == EquipmentSlotKind.Tool || slot == EquipmentSlotKind.Unknown) return result;

            Span<int> legal = stackalloc int[16];
            int count = AffixRegistry.GetLegalAffixIndices(slot, legal);
            var defs = AffixRegistry.Definitions;
            for (int i = 0; i < count; i++) result.Add(defs[legal[i]].Id);
            return result;
        }

        public static bool IsLegalAffix(string baseItemId, string? affixId)
            => !string.IsNullOrEmpty(affixId) && LegalAffixIds(baseItemId).Contains(affixId);

        /// <summary>
        /// The finished piece's rarity: the floor, or better if the ordinary
        /// zero-luck drop roll comes up higher.
        /// </summary>
        /// <remarks>
        /// Modul: A FLOOR, NOT A FIXED TIER. The same RollTier every monster
        /// drop reads (luck 0 - a Workshop has no Fortune), clamped up to the
        /// floor. So the median result IS the floor (a zero-luck roll lands at
        /// or below Common 76% of the time), and a commission can still
        /// surprise - roughly one in a hundred Epic floors comes out Legendary
        /// or better - without a second rarity table to drift from the drops.
        /// CraftingEngine.RollCraftedRarity, the old "Workshop multiplier" that
        /// nothing ever called, is deliberately not used: it is a second table.
        /// </remarks>
        public static int ResolveTier(int floorTier, Random rng)
        {
            int rolled = RarityTier.RollTier(0f, rng);
            return Math.Clamp(Math.Max(floorTier, rolled), RarityTier.Normal, RarityTier.Transcendent);
        }

        /// <summary>
        /// The finished piece's affixes: the chosen one at COMMON affix rarity,
        /// then the rest of the tier's count rolled exactly as a drop's are.
        /// </summary>
        /// <remarks>
        /// Modul: THE CHOSEN AFFIX IS ONE OF THE ITEM'S AFFIXES, NOT AN EXTRA.
        /// RarityTier.GetAffixCount is the one authority on how many affixes a
        /// tier carries, and fusion, the reroll and the validator all read
        /// items through it; an Epic with three affixes would be the only item
        /// in the game breaking that rule. So a T6 piece carries two: the one
        /// the player chose, and one rolled. The rolled ones go through
        /// TryRollOneAdditional - legal for the slot, a stat the item lacks
        /// preferred, a canonical "id@rarity" key that stacks rather than
        /// collides - so every affix on a commissioned piece is rerollable.
        /// The chosen key is canonical too ("crit_chance_pct@1"): the fusion's
        /// old "_xxxx" format is what made affixes unrerollable once.
        /// </remarks>
        public static Dictionary<string, int> BuildAffixes(string baseItemId, int region, int tier, string chosenAffixId)
        {
            var affixes = new Dictionary<string, int>(StringComparer.Ordinal);
            if (AffixRegistry.TryGetDefinition(chosenAffixId, out var chosen))
            {
                affixes[AffixRegistry.BuildPayloadKey(chosen.Id, 1, AffixRarity.Common)] =
                    AffixRegistry.RollMagnitude(chosen, region, AffixRarity.Common);
            }

            int total = RarityTier.GetAffixCount(tier);
            for (int i = affixes.Count; i < total; i++)
            {
                if (!AffixRegistry.TryRollOneAdditional(baseItemId, region, tier, affixes.Keys, out string key, out int magnitude)) break;
                affixes[key] = magnitude;
            }
            return affixes;
        }
    }
}
