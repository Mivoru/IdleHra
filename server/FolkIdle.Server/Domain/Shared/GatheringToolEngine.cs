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
    //   1: Birch Tools             +35 percent speed  (Tier 1 gear band)
    //   2: Golden Birch Tools      +82 percent
    //   3: Willow Tools           +146 percent        (Tier 2 gear band)
    //   4: Whispering Willow Tools +232 percent
    //   5: Acacia Tools           +348 percent        (Tier 3 gear band)
    //   6: Ironwood Tools         +505 percent
    //   7: Frostpine Tools        +717 percent        (Tier 4 gear band)
    //   8: Glacier Pine Tools    +1003 percent
    //   9: Ebon Tools            +1390 percent        (Tier 5 gear band)
    //  10: Void Bark Tools       +1912 percent
    //
    // Modul: THE CURVE IS GEOMETRIC NOW, 1.35x a tier, where it used to run
    // +10% to +200% - a top-tier tool being three times a bare hand, and only
    // 2.7 times the very first tool a player crafts.
    //
    // That flatness is why gathering grew into most of the playtime. Food
    // demand scales with how hard monsters hit, and monsters were raised
    // steeply on purpose; fishing speed improved by almost nothing across the
    // same five regions, so the gap became the game. A tool has to be worth
    // gathering the wood and ore to build it, and at +10 percent it was not.
    //
    // The within-region upgrade is what a player actually feels: every band is
    // two tiers, and the second is about 1.35x the first whichever band they
    // are in - a steady reason to go back to the forge rather than a payoff
    // that only exists at the end of the game.
    // The same table covers Axes, Pickaxes, and Fishing Rods - the tool
    // tier accelerates whichever gathering profession the active node
    // belongs to.
    public static class GatheringToolEngine
    {
        public const int MinRequiredTicks = 2;
        public const int VillageYieldBonusPctPerLevel = 5;

        // Pure integer switch - zero allocation, safe on the 10Hz tick.
        // Tabulated rather than computed so the tick does no floating-point
        // work; the values are 100 * (1.35^tier - 1), rounded.
        public static int GetToolSpeedBonusPct(int toolTier)
        {
            return toolTier switch
            {
                1 => 35,
                2 => 82,
                3 => 146,
                4 => 232,
                5 => 348,
                6 => 505,
                7 => 717,
                8 => 1003,
                9 => 1390,
                10 => 1912,
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
            => ComputeRequiredTicks(baseTickThreshold, masteryLevel, toolTier, villageProductionLevel, 0);

        /// <param name="toolAffixSpeedPct">
        /// The gather_speed_pct rolled on the tools the character is WEARING.
        ///
        /// Modul: this parameter is new, and its absence was a dead end.
        /// StateCheckpointManager computed the figure off the equipped tools,
        /// stored it on the payload as ToolGatherSpeedPct and shipped it to the
        /// client - and no code anywhere read it back. Every gather-speed affix
        /// ever rolled did nothing at all, which is the same shape of defect as
        /// the five "output side never wired" ones found before it: the input
        /// half is complete, convincing, and connected to nothing.
        /// </param>
        // Mastery accelerates gathering the same way everything else does - as a
        // percentage.
        //
        // Modul: it used to SUBTRACT two ticks per level, flat, before any
        // multiplier applied. On region 1's 30-tick node that goes negative at
        // mastery 15 and clamps to the two-tick minimum, which is what put
        // "0.2s / unit (floor)" on the gathering screen: the first two regions
        // gathered instantly, and no tool, village building or affix could
        // change a number that was already pinned to the bottom.
        //
        // A subtraction cannot be balanced against a threshold it does not
        // know. Ten percent a level compounds with the tool curve instead of
        // racing it to the floor.
        public const int MasterySpeedPctPerLevel = 10;

        public static int ComputeRequiredTicks(int baseTickThreshold, int masteryLevel, int toolTier, int villageProductionLevel, int toolAffixSpeedPct)
        {
            int ticks = baseTickThreshold;
            if (ticks < MinRequiredTicks)
            {
                return MinRequiredTicks;
            }

            int totalSpeedBonusPct = GetToolSpeedBonusPct(toolTier);
            if (masteryLevel > 0)
            {
                totalSpeedBonusPct += masteryLevel * MasterySpeedPctPerLevel;
            }
            if (villageProductionLevel > 0)
            {
                totalSpeedBonusPct += villageProductionLevel * VillageYieldBonusPctPerLevel;
            }
            if (toolAffixSpeedPct > 0)
            {
                totalSpeedBonusPct += toolAffixSpeedPct;
            }

            if (totalSpeedBonusPct > 0)
            {
                ticks = ticks * 100 / (100 + totalSpeedBonusPct);
            }

            return ticks < MinRequiredTicks ? MinRequiredTicks : ticks;
        }
    }
}
