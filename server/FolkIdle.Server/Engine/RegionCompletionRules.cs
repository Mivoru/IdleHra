using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// When a region counts as COMPLETED - the rule behind CompletedAreaFlags,
    /// the +1% loot luck per region StatsCalculator grants for it, and the
    /// PlayerRegionCompletions ledger. The login recompute, the codex worker
    /// and the codex regions endpoint all ask here.
    /// </summary>
    /// <remarks>
    /// Modul: A LEVER NO PLAYER COULD EVER PULL (task 26, H5, fixed 2026-09-24).
    ///
    /// Three copies of the rule grouped monsters by GetMonsterRegionTier, and
    /// monsters.json still holds the 90 legacy monsters (ids 1-90) that nobody
    /// can fight - they carried RegionTier 1-10 too. "Every monster in region 1
    /// has 1,000 codex kills" therefore included monsters with no way to be
    /// killed, no region could ever complete, and the area-luck term was 0 for
    /// every player in the game while being computed, persisted, put on the
    /// wire and drawn as a progress bar. Production had 0 completion rows.
    ///
    /// Decided with the owner: the five CANONICAL regions only (ids 91-115,
    /// five each), 1,000 kills of each regular and 100 of the boss - a boss is
    /// one monster in five and takes several times as long to kill, so 1,000
    /// of it was a wall nobody had got near (the heaviest account: 2 / 8 / 33
    /// / 2 / 0). Worth at most +5 loot luck, about +3% relative Ancient+.
    /// </remarks>
    public static class RegionCompletionRules
    {
        public const int RegularKillsRequired = 1000;
        public const int BossKillsRequired = 100;

        /// <summary>Kills of this monster a completion needs, or 0 for a monster in no region.</summary>
        public static int RequiredKills(int monsterId)
        {
            if (ContentRegistry.GetCanonicalLocation(monsterId) == 0) return 0;
            return ContentRegistry.IsRegionalBoss(monsterId) ? BossKillsRequired : RegularKillsRequired;
        }

        /// <summary>The first canonical monster id of a location (1-5).</summary>
        public static int FirstMonsterOf(int location)
            => ContentRegistry.FirstCanonicalMonsterId + (location - 1) * ContentRegistry.MonstersPerRegion;

        /// <summary>Whether every monster of <paramref name="location"/> has its required kills.</summary>
        public static bool IsComplete(int location, Func<int, int> killsFor)
        {
            if (location < 1 || location > ContentRegistry.LocationCount) return false;

            int first = FirstMonsterOf(location);
            for (int id = first; id < first + ContentRegistry.MonstersPerRegion; id++)
            {
                if (killsFor(id) < RequiredKills(id)) return false;
            }
            return true;
        }

        /// <summary>CompletedAreaFlags for a codex: bit <c>location</c> (1-5) per completed region.</summary>
        public static int CompletedFlags(Func<int, int> killsFor)
        {
            int flags = 0;
            for (int location = 1; location <= ContentRegistry.LocationCount; location++)
            {
                if (IsComplete(location, killsFor)) flags |= 1 << location;
            }
            return flags;
        }
    }
}
