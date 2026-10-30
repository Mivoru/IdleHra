namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Craft points, the Hearth half's currency (roadmap 7.4, step 4.2).
    /// </summary>
    /// <remarks>
    /// Modul: DERIVED, NEVER STORED. Earned is a function of the gathering
    /// mastery the account already keeps, and spent will be the sum of the
    /// Hearth rows. A stored balance is a second copy of that truth, and
    /// RespecAsync shows the lesson: it recomputes rather than trusting a
    /// counter, because a counter drifts the first time a write is lost.
    ///
    /// The rate (owner decision D2, 2026-10-30) is one point per ten summed
    /// levels of Woodcutting, Mining, Fishing and Herbalism. Measured on
    /// production that day it gave the three active accounts (levels 98-111)
    /// 80-93 points, about one per account level, which is the War half's pace.
    /// Mastery has no cap and costs 50 x (level + 1)^2 a level, so the pace
    /// slows for a player who keeps gathering and never speeds up.
    ///
    /// Cooking and Alchemy are left out on purpose: nothing advances their
    /// mastery, so counting them would price a number nobody can raise.
    /// Mastery survives a rebirth, so these points do too.
    /// </remarks>
    public static class HearthPoints
    {
        /// <summary>Summed gathering mastery levels per craft point.</summary>
        public const int MasteryLevelsPerPoint = 10;

        /// <summary>Craft points this account has earned from its gathering mastery.</summary>
        public static int Earned(int woodcutting, int mining, int fishing, int herbalism)
        {
            long summed = (long)System.Math.Max(0, woodcutting) + System.Math.Max(0, mining)
                + System.Math.Max(0, fishing) + System.Math.Max(0, herbalism);
            return (int)System.Math.Min(int.MaxValue, summed / MasteryLevelsPerPoint);
        }

        /// <summary>Craft points earned, read off the live payload's mastery fields.</summary>
        public static int Earned(in TickStatePayload payload)
            => Earned(payload.WoodcuttingMasteryLevel, payload.MiningMasteryLevel,
                payload.FishingMasteryLevel, payload.HerbalismMasteryLevel);
    }
}
