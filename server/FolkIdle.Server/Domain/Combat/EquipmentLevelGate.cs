using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    // Modul: Advanced Economy Refactoring, Part 2.2. Single authoritative
    // derivation of an equipment item's RequiredLevel from its structural
    // power bounds - the same two axes that already scale every stat this
    // codebase generates (CombatLootEngine's affix magnitudes and
    // AffixRerollEngine's reroll bounds both derive strictly from
    // RegionTier and QualityTier), so the level gate is anchored to the
    // exact inputs that make an item powerful rather than a separate,
    // driftable hand-authored table.
    //
    // Formula: (RegionTier - 1) * 10 + QualityTier * 2. Each region step
    // is a 10-level band (region 1 gear from level 0, region 10 gear from
    // level 90), and each forge quality tier inside a region adds 2 levels
    // (a fully Transcendent T13 item adds +26 - forged power counts toward
    // the gate, closing the cheese where a region-1 item forged to a high
    // tier carries region-appropriate but tier-inflated stats to a fresh
    // character). Pure integer arithmetic - zero allocation, callable from
    // any hot path.
    public static class EquipmentLevelGate
    {
        public const int LevelsPerRegionTier = 10;
        public const int LevelsPerQualityTier = 2;

        public static int DeriveRequiredLevel(int regionTier, int qualityTier)
        {
            if (regionTier < 1)
            {
                regionTier = 1;
            }
            if (qualityTier < 0)
            {
                qualityTier = 0;
            }

            return (regionTier - 1) * LevelsPerRegionTier + qualityTier * LevelsPerQualityTier;
        }

        // Convenience overload resolving the item's RegionTier through
        // ContentRegistry. Unknown base ids gate at level 0 (never blocks) -
        // a missing definition is a content bug ContentRegistry's own boot
        // validation already fails loudly on, not something to silently
        // brick a player's purchase over at runtime.
        public static int DeriveRequiredLevel(string baseItemId, int qualityTier)
        {
            if (!ContentRegistry.TryGetItemDefinitionByBaseId(baseItemId, out var definition))
            {
                return 0;
            }

            return DeriveRequiredLevel(definition.RegionTier, qualityTier);
        }
    }
}
