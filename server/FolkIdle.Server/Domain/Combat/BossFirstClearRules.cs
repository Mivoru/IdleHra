using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat
{
    /// <summary>
    /// A region boss is an EVENT the first time and a chore afterwards.
    ///
    /// Reported from play: "I killed the first boss almost without fish, and my
    /// gear was Field Mouse and Horned Rabbit drops at about rarity 2". The
    /// answer is not simply a bigger boss - a boss sized to need a full set of
    /// high-rarity gear is then a wall every time a player wants the thing it
    /// drops, and farming a wall is not fun, it is a tax.
    ///
    /// So the first kill and every later kill are different fights. Until a
    /// player has put a boss down once, it carries five times the health and
    /// twice the attack. After that it reverts to its authored stats, which are
    /// already a real fight, and can be farmed.
    ///
    /// The state this reads is the SAME state that unlocks regions - a boss is
    /// beaten or it is not, recorded in the monster codex and cached onto the
    /// payload as a five-bit mask. Nothing new is stored, and a player cannot
    /// lose the achievement by dying afterwards.
    /// </summary>
    public static class BossFirstClearRules
    {
        public const int FirstClearHpMultiplier = 5;
        public const int FirstClearAttackMultiplier = 2;

        /// <summary>
        /// The region a monster is the boss OF, or 0 when it is not a region
        /// boss. Asked of the unlock registry rather than derived from the id,
        /// for the same reason RegionUnlockGate asks it: the arithmetic
        /// convention that "every fifth monster is a boss" is true of the
        /// canonical 25 and has silently mis-classified content before.
        /// </summary>
        public static int RegionOfBoss(int monsterId)
            => RaceUnlockRegistry.GetRegionForBossMonsterId(monsterId);

        private static int BitFor(int region) => 1 << (region - RaceUnlockRegistry.FirstRegion);

        public static bool IsDefeated(byte defeatedMask, int monsterId)
        {
            int region = RegionOfBoss(monsterId);
            return region != 0 && (defeatedMask & BitFor(region)) != 0;
        }

        /// <summary>
        /// True only for a region boss this player has never put down. Ordinary
        /// monsters are never a "first clear" - they have no such thing.
        /// </summary>
        public static bool IsFirstClearPending(byte defeatedMask, int monsterId)
        {
            int region = RegionOfBoss(monsterId);
            return region != 0 && (defeatedMask & BitFor(region)) == 0;
        }

        public static byte MarkDefeated(byte defeatedMask, int monsterId)
        {
            int region = RegionOfBoss(monsterId);
            return region == 0 ? defeatedMask : (byte)(defeatedMask | BitFor(region));
        }

        public static byte MaskFrom(System.Collections.Generic.IReadOnlySet<int> defeatedBossMonsterIds)
        {
            byte mask = 0;
            foreach (int monsterId in defeatedBossMonsterIds)
            {
                mask = MarkDefeated(mask, monsterId);
            }
            return mask;
        }

        /// <summary>
        /// Monster health as this player meets it. Every site that starts a
        /// fight goes through here - there were four, and a first-clear boss
        /// that is only bigger on some of them is a bug that shows up as a
        /// health bar jumping when a player switches targets and comes back.
        /// </summary>
        public static long MaxHpFor(byte defeatedMask, int monsterId)
        {
            long baseHp = ContentRegistry.GetScaledMonsterMaxHp(monsterId);
            return IsFirstClearPending(defeatedMask, monsterId)
                ? baseHp * FirstClearHpMultiplier
                : baseHp;
        }

        public static long AttackPowerFor(byte defeatedMask, int monsterId)
        {
            long baseAttack = ContentRegistry.GetScaledMonsterAttackPower(monsterId);
            return IsFirstClearPending(defeatedMask, monsterId)
                ? baseAttack * FirstClearAttackMultiplier
                : baseAttack;
        }
    }
}
