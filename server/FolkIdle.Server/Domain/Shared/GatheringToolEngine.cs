using System;
using FolkIdle.Server.Engine;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Domain.Shared
{
    // Modul: Deferred Part 5 Implementation, Part 1. Gathering tool speed
    // scaling - the pure math of the gathering tick's required-tick
    // computation, extracted from SimulationEngine's inline formula into a
    // testable, allocation-free static function set. There is no
    // 'ActivityRoutingEngine.cs' in this codebase (the task names one) -
    // the gathering execution pipeline is SimulationEngine's
    // TryGetGatheringNode branch on the 10Hz tick, which now calls
    // ComputeRequiredTicks below.
    //
    // Tool tiers are the unmanaged int the payload already carries
    // (TickStatePayload.CachedCurrentToolTier) - no string ids anywhere
    // near the tick path, per the zero-allocation constraint. The ten
    // named tool families map onto tiers 1-10:
    //   1: Birch Tools            +10 percent speed  (Tier 1 gear band)
    //   2: Golden Birch Tools     +20 percent speed
    //   3: Willow Tools           +25 percent speed  (Tier 2 gear band)
    //   4: Whispering Willow Tools +40 percent speed
    //   5: Acacia Tools           +50 percent speed  (Tier 3 gear band)
    //   6: Ironwood Tools         +75 percent speed
    //   7: Frostpine Tools        +85 percent speed  (Tier 4 gear band)
    //   8: Glacier Pine Tools     +120 percent speed
    //   9: Ebon Tools             +150 percent speed (Tier 5 gear band)
    //  10: Void Bark Tools        +200 percent speed
    // The same table covers Axes, Pickaxes, and Fishing Rods - the tool
    // tier accelerates whichever gathering profession the active node
    // belongs to.
    public static class GatheringToolEngine
    {
        public const int MinRequiredTicks = 2;
        public const int VillageYieldBonusPctPerLevel = 5;

        // Pure integer switch - zero allocation, safe on the 10Hz tick.
        public static int GetToolSpeedBonusPct(int toolTier)
        {
            return toolTier switch
            {
                1 => 10,
                2 => 20,
                3 => 25,
                4 => 40,
                5 => 50,
                6 => 75,
                7 => 85,
                8 => 120,
                9 => 150,
                10 => 200,
                _ => 0
            };
        }

        // The full gathering-speed computation: the legacy flat reductions
        // (mastery, tool tier) preserved exactly, then the tool family's
        // percentage speed multiplier, then the village production
        // building's +5 percent per level (Lumber Mill for Woodcutting,
        // Mine Depot for Mining - Deferred Part 5, Part 3). All integer
        // arithmetic; a +X percent speed bonus divides the tick
        // requirement by (100 + X)/100, floored at MinRequiredTicks.
        public static int ComputeRequiredTicks(int baseTickThreshold, int masteryLevel, int toolTier, int villageProductionLevel)
        {
            int ticks = baseTickThreshold - (masteryLevel * 2) - toolTier;
            if (ticks < MinRequiredTicks)
            {
                return MinRequiredTicks;
            }

            int totalSpeedBonusPct = GetToolSpeedBonusPct(toolTier);
            if (villageProductionLevel > 0)
            {
                totalSpeedBonusPct += villageProductionLevel * VillageYieldBonusPctPerLevel;
            }

            if (totalSpeedBonusPct > 0)
            {
                ticks = ticks * 100 / (100 + totalSpeedBonusPct);
            }

            return ticks < MinRequiredTicks ? MinRequiredTicks : ticks;
        }
    }
}
