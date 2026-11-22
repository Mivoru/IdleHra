using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// The base XP a kill pays this character: half for a monster two or more
    /// regions below the highest region the account has opened.
    /// </summary>
    /// <remarks>
    /// Modul: THE MIDDLE-GAME FARM-DOWN TRAP (owner decision 2026-11-22, option A).
    /// XP per monster health point FALLS with the region in monsters.json -
    /// 0.200 in region 1, 0.147, 0.136, 0.127, 0.118 in region 5 - while armour
    /// rises. A character that needs several swings per kill therefore earned
    /// most on the lowest region it farmed quickly: bot runs B and C put every
    /// optimal bot on Alpha Wolf (95) at level 61-70, with region 3 open, for
    /// 6-9 online hours per stall (docs/design/PLAYER_PATH.md, open findings).
    /// Halving region 1 there (0.100) puts it under region 3 (0.136).
    ///
    /// Why a cut and not new content numbers: raising regions 2-5 would multiply
    /// the XP of the endgame accounts that one-shot Malakor (paid per kill, not
    /// per HP), and lowering regions 1-2 would slow every new player's first
    /// day. This touches neither: a new player has nothing two regions below
    /// it, and a Malakor farmer stands in region 5.
    ///
    /// ONE place, every path: the live kill (RunCombatTick), its kill event,
    /// and HuntingProjection.XpPerKill, which the hunting advisor and the
    /// offline window both price with. Only character XP is cut; seasonal
    /// (Chronicle) and codex XP keep the content reward.
    /// </remarks>
    public static class LowRegionXp
    {
        /// <summary>How many regions below the highest open one a monster must be to pay the cut rate.</summary>
        public const int RegionsBelow = 2;

        /// <summary>The share of the base XP such a kill pays.</summary>
        public const int PaidPct = 50;

        public static bool Applies(in TickStatePayload payload, int monsterId)
            => ContentRegistry.GetMonsterRegionTier(monsterId) <= payload.HighestUnlockedRegion - RegionsBelow;

        public static int BaseXp(in TickStatePayload payload, in MonsterDefinition monster)
            => Applies(in payload, monster.Id) ? monster.BaseXpReward * PaidPct / 100 : monster.BaseXpReward;
    }
}
