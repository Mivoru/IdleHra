using System;
using System.Collections.Concurrent;
using FolkIdle.Server.Engine;

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

        /// <summary>"Mivoru rerolled Legendary crit dmg%".</summary>
        public static string Reroll(string who, AffixRarity rarity, string affixId)
            => $"{who} rerolled {rarity} {AffixLabel(affixId)}";

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

        // Modul: the affix id, not a raw magnitude. The old reroll line printed
        // "(+185)" for a Percentage affix, whose magnitude is in TENTHS of a
        // percent - so the world was told 185 where the item says 18.5%. The
        // rarity is the news; the number was wrong and long.
        internal static string AffixLabel(string affixId)
        {
            if (string.IsNullOrEmpty(affixId)) return "affix";

            string label = affixId;
            if (label.StartsWith("flat_", StringComparison.Ordinal)) label = label.Substring(5);
            if (label.EndsWith("_pct", StringComparison.Ordinal)) label = label.Substring(0, label.Length - 4) + "%";
            return label.Replace('_', ' ');
        }
    }

    // Modul: WHY A REROLL IS NOT ANNOUNCED EVERY TIME IT IS GOOD, 2026-10-02.
    //
    // Reported by the owner as "when I reroll it spams the chat". It did, by
    // arithmetic: an Epic-or-better affix is 50 rolls in 1000
    // (AffixRegistry._rarityWeightsPerMille), a reroll is one tap, and
    // auto-reroll fires up to its attempt cap in one press - every Epic it
    // passed THROUGH was announced, including the ones the very next attempt
    // destroyed. A gamble the player repeats hundreds of times cannot use the
    // threshold of a drop, which happens to them once.
    //
    // Three rules, in this order:
    //   1. Legendary only (10 in 1000). Epic still glows on the item; it just
    //      is not world news when it is one roll in twenty.
    //   2. At most one reroll announcement per player per PerPlayerCooldown.
    //      A player who lands two Legendaries in a minute is one event to
    //      everybody else, and nobody's luck should be able to fill the
    //      channel.
    //   3. Auto-reroll announces once, the run's FINAL result - what the item
    //      actually holds - and none of the attempts in between
    //      (AffixRerollEngine.ExecuteAutoRerollAsync).
    //
    // Only reroll announcements pass through here. Drops, boss clears and
    // season placements keep their own rules; they are rare by construction.
    public static class RerollAnnouncementPolicy
    {
        public const AffixRarity MinimumRarity = AffixRarity.Legendary;

        public static readonly TimeSpan PerPlayerCooldown = TimeSpan.FromMinutes(10);

        // Static for the same reason ChatEngine.SystemAnnouncementQueue is:
        // the engine that rolls is a singleton and the claim has to hold
        // across concurrent reroll commands for one player. Grows by one
        // entry per player who has ever been announced - bounded by the
        // player count, and only by the lucky part of it.
        private static readonly ConcurrentDictionary<long, long> _lastAnnouncedUtcTicks = new();

        /// <summary>
        /// True when this result should be announced, and records the claim so
        /// the next one inside the cooldown is refused. Atomic per player: two
        /// concurrent Legendaries for one player announce once.
        /// </summary>
        public static bool TryClaim(long playerId, AffixRarity rarity, DateTime utcNow)
        {
            if (rarity < MinimumRarity) return false;

            long now = utcNow.Ticks;
            while (true)
            {
                if (!_lastAnnouncedUtcTicks.TryGetValue(playerId, out long last))
                {
                    if (_lastAnnouncedUtcTicks.TryAdd(playerId, now)) return true;
                    continue;
                }

                if (now - last < PerPlayerCooldown.Ticks) return false;
                if (_lastAnnouncedUtcTicks.TryUpdate(playerId, now, last)) return true;
            }
        }
    }
}
