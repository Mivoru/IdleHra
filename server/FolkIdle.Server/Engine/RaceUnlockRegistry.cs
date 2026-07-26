using System;

namespace FolkIdle.Server.Engine
{
    // Modul: race unlocks. The single authority on which region boss unlocks
    // which playable race.
    //
    // Why this exists: the game ships six races (see RaceIds) and every player
    // could only ever have Human ones. A new account is created with
    // GeneticVector = RaceIds.Human, the only other way a character comes into
    // existence is BreedingEngine, and BreedingEngine refuses any pair whose
    // LocusRace.Dominant values differ. So Vila, Draugr, Kobold, Vodnik and
    // Moosleute were unreachable content with no acquisition path at all -
    // authored stats, racial passives and mastery tracks that no player could
    // ever see, exactly like the gathering nodes that dropped nothing.
    //
    // The mapping is one race per region boss, and it fits exactly: five
    // canonical regions, five bosses, five races beyond the Human starter.
    // Boss monster ids are 90 + region * 5 (95 Alpha Wolf, 100 Shadow Lynx,
    // 105 Magma Wyrm, 110 Frost Titan, 115 Malakor) - the last id in each
    // region's block of five, per the canonical content layout.
    public static class RaceUnlockRegistry
    {
        // The five canonical regions' boss monster ids, ordered by region.
        public const int FirstRegion = 1;
        public const int LastRegion = 5;

        public static int GetRegionBossMonsterId(int regionTier)
        {
            if (regionTier < FirstRegion || regionTier > LastRegion) return 0;
            return 90 + regionTier * 5;
        }

        // Which race a given monster's first kill unlocks, or 0 if that monster
        // is not a region boss. Deliberately keyed on the boss id rather than
        // derived from RegionTier: RegionTier spans 1-10 across the 90 legacy
        // monsters that are not canonical progression content, and deriving
        // from it is the mistake that once pulled 41 creatures into region 1.
        public static byte GetRaceUnlockedByBoss(int monsterId)
        {
            return monsterId switch
            {
                95 => RaceIds.Vila,        // Region 1 - Alpha Wolf
                100 => RaceIds.Draugr,     // Region 2 - Shadow Lynx
                105 => RaceIds.Kobold,     // Region 3 - Magma Wyrm
                110 => RaceIds.Vodnik,     // Region 4 - Frost Titan
                115 => RaceIds.Moosleute,  // Region 5 - Malakor
                _ => 0
            };
        }

        // Human needs no unlock - every account starts with it.
        public static bool IsUnlockedByDefault(byte raceId) => raceId == RaceIds.Human;

        public static bool IsPlayableRace(byte raceId) => raceId >= RaceIds.Human && raceId <= RaceIds.Moosleute;

        // Display name for the unlock notification and the roster UI.
        public static string GetRaceName(byte raceId)
        {
            return raceId switch
            {
                RaceIds.Human => "Human",
                RaceIds.Vila => "Vila",
                RaceIds.Draugr => "Draugr",
                RaceIds.Kobold => "Kobold",
                RaceIds.Vodnik => "Vodnik",
                RaceIds.Moosleute => "Moosleute",
                _ => "Unknown"
            };
        }
    }
}
