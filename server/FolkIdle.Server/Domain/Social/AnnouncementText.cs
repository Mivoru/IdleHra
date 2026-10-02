namespace FolkIdle.Server.Domain.Social
{
    // Modul: EVERY WORLD ANNOUNCEMENT IS WORDED HERE, 2026-10-02.
    //
    // Four engines each wrote their own sentence, and every one of them ended
    // in "Congratulations!" - a word the client already says with its gz!
    // button beside the line. The longest of them (a drop: who, rarity, item,
    // "from" the monster, the congratulation) ran past ResponseChatMessagePacket's
    // 128-byte MessageText buffer with a long item and boss name, so the line
    // arrived cut off mid-word. The owner's report was simply "the
    // announcements are too long".
    //
    // One compact line each, no trailing congratulation, no clause the player
    // does not need to recognise the event. Kept in one file so the next
    // announcement is written in the same voice, and so AnnouncementTextTests
    // can hold every format under the wire's byte budget at once.
    public static class AnnouncementText
    {
        /// <summary>"Mivoru found Mythic Iron Sword".</summary>
        public static string Drop(string who, string rarityName, string itemName)
            => $"{who} found {rarityName} {itemName}";

        /// <summary>
        /// "WORLD FIRST: Mivoru beat Malakor" / "Mivoru beat Malakor". The
        /// announcer only ever fires on a player's FIRST clear, so "for the
        /// first time" was a clause restating when the line is sent at all.
        /// </summary>
        public static string BossClear(string who, string bossName, bool worldFirst)
            => worldFirst ? $"WORLD FIRST: {who} beat {bossName}" : $"{who} beat {bossName}";

        /// <summary>"Season 4: #1 Mivoru". The band name only restated the rank.</summary>
        public static string SeasonPlacement(int eraId, int rank, string who)
            => $"Season {eraId}: #{rank} {who}";
    }
}
