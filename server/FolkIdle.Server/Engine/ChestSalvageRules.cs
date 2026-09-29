namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The chest's auto-sell rules (task 81): one floor for every region
    /// (<c>PlayerRecord.AutoSalvageBelowTier</c>) plus an optional higher floor
    /// per region (<c>PlayerRecord.AutoSalvageRegionTiers</c>).
    /// </summary>
    /// <remarks>
    /// Modul: A REGION RULE CAN ONLY RAISE THE FLOOR, never lower it. The
    /// effective tier for a drop is the larger of the two. That keeps one
    /// meaning for 0 in both places ("no rule here"), so no sentinel is needed
    /// for "this region ignores the global rule", and it never makes a new
    /// setting keep less than the old one did. A player who wants to sell less
    /// in region 5 lowers the global floor and raises it where they want it.
    ///
    /// Modul: PACKED INTO ONE INT, four bits a region. Every tier a rule may
    /// hold is at most VillageChestEngine.MaxSweepableQualityTier (6), and the
    /// value rides TickStatePayload and CombatLootDropRequest, which are
    /// structs copied by value on the tick. An array field there would be a
    /// shared reference inside a value type. Only this class knows the layout.
    ///
    /// Locked pieces never need a rule. A rule runs on the way IN, before a row
    /// exists, and nothing can lock a drop that has not landed yet.
    /// </remarks>
    public static class ChestSalvageRules
    {
        /// <summary>The five canonical regions a rule can name.</summary>
        public const int RegionCount = 5;

        private const int BitsPerRegion = 4;
        private const int RegionMask = (1 << BitsPerRegion) - 1;

        /// <summary>The floor one region's rule sets, or 0 when it has none.</summary>
        public static int RegionTier(int packed, int region)
        {
            if (region < 1 || region > RegionCount) return 0;
            return (packed >> ((region - 1) * BitsPerRegion)) & RegionMask;
        }

        /// <summary>
        /// Packs one floor per region (index 0 is region 1). Returns false,
        /// without packing, when there are not exactly five values or any value
        /// is outside 0..MaxSweepableQualityTier. The route refuses a bad
        /// value instead of clamping it, because this setting destroys items.
        /// </summary>
        public static bool TryPack(System.Collections.Generic.IReadOnlyList<int> tiers, out int packed)
        {
            packed = 0;
            if (tiers == null || tiers.Count != RegionCount) return false;
            for (int i = 0; i < RegionCount; i++)
            {
                int tier = tiers[i];
                if (tier < 0 || tier > VillageChestEngine.MaxSweepableQualityTier) return false;
                packed |= tier << (i * BitsPerRegion);
            }
            return true;
        }

        public static int[] Unpack(int packed)
        {
            var tiers = new int[RegionCount];
            for (int i = 0; i < RegionCount; i++) tiers[i] = RegionTier(packed, i + 1);
            return tiers;
        }

        /// <summary>
        /// Clamps every region's floor into range. Used at hydration, so a row
        /// written by hand or before the cap existed cannot sell a player's
        /// Legendaries. It does the same job as the clamp on AutoSalvageBelowTier.
        /// </summary>
        public static int Sanitise(int packed)
        {
            var tiers = Unpack(packed);
            for (int i = 0; i < RegionCount; i++)
            {
                tiers[i] = System.Math.Clamp(tiers[i], 0, VillageChestEngine.MaxSweepableQualityTier);
            }
            TryPack(tiers, out int clean);
            return clean;
        }

        /// <summary>
        /// The tier at or below which a drop from <paramref name="region"/> is
        /// sold on the way in. 0 means keep everything.
        /// </summary>
        public static int EffectiveTier(int globalTier, int packedRegionTiers, int region)
        {
            return System.Math.Max(globalTier, RegionTier(packedRegionTiers, region));
        }
    }
}
