using System;
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
    /// player has put a boss down once it carries its region's wall - between 3x
    /// and 14x its health, and between 2.6x and 21.4x its attack, rising with the
    /// region. After that it reverts to its authored stats, which are already a
    /// real fight, and can be farmed.
    ///
    /// The multipliers were a flat 5x/2x for every boss until 2026-09-12, which
    /// is why a full set of region-4 gear could beat Malakor. They are solved
    /// against a projected reference character now - see BossWallTests.
    ///
    /// The state this reads is the SAME state that unlocks regions - a boss is
    /// beaten or it is not, recorded in the monster codex and cached onto the
    /// payload as a five-bit mask. Nothing new is stored, and a player cannot
    /// lose the achievement by dying afterwards.
    /// </summary>
    public static class BossFirstClearRules
    {
        // Modul: THE WALL IS PER-REGION NOW, 2026-09-12.
        //
        // It was a flat 5x health and 2x attack for all five bosses, and one
        // pair of numbers cannot be a real fight at level 10 and at level 100
        // against gear that has grown by two orders of magnitude in between.
        // Reported by the developer playing his own game: a full set of
        // RegionTier 4 gear was beating MALAKOR, the last monster in the game,
        // the same day it beat the region-4 boss.
        //
        // These numbers are not guesses and must not be edited as if they were.
        // BossWallTests projects a reference character (BossGearBenchmark)
        // against every boss and asserts where the break-even falls: the
        // region's own gear at RequiredQualityTierFor wins, two tiers below
        // loses, and a full set one region BEHIND loses at every quality tier in
        // the game. Change a multiplier and that test tells you what you did.
        //
        // Region 1 is deliberately the gentlest. A fresh account must beat it to
        // reach region 2 at all, and "a new player died to the first monster and
        // onboarding stalled there forever" is a defect this repo has already
        // shipped once.
        private static readonly float[] _hpMultiplierByRegion = { 3.0f, 4.0f, 6.0f, 9.0f, 14.0f };
        private static readonly float[] _attackMultiplierByRegion = { 3.7f, 2.6f, 5.7f, 11.4f, 21.4f };

        // The gear each boss is calibrated to need: all eight combat slots of
        // the boss's OWN RegionTier, at this QualityTier, with affixes of at
        // least this rarity. Agreed with the developer and recorded in
        // docs/superpowers/specs/2026-09-12-boss-gear-wall-design.md.
        //
        // Published as code rather than left implicit in the tuning so the Wiki
        // and the Combat screen can state the requirement later without
        // re-deriving it - an undocumented wall is indistinguishable from a bug,
        // which is how the last one was reported.
        // Modul: region 2 asks for 7, not the 6 first sketched. Affix COUNT steps
        // at quality 4, 7, 10 and 13 (RarityTier.GetAffixCount), and 4 and 6 both
        // carry two - so "quality 6 wins, quality 4 loses" was a 4% tuning window
        // between two mechanically identical sets, which is a coin flip dressed as
        // a requirement. Seven is the first tier that carries a third affix, so
        // the bar lands on a step the game actually has. Every other row already
        // crossed one.
        private static readonly int[] _requiredQualityTierByRegion = { 4, 7, 8, 10, 11 };

        private static readonly Engine.AffixRarity[] _requiredAffixRarityByRegion =
        {
            Engine.AffixRarity.Common,
            Engine.AffixRarity.Common,
            Engine.AffixRarity.Rare,
            Engine.AffixRarity.Epic,
            Engine.AffixRarity.Legendary
        };

        private static int IndexOf(int region)
            => Math.Clamp(region - RaceUnlockRegistry.FirstRegion, 0, _hpMultiplierByRegion.Length - 1);

        public static float HpMultiplierFor(int region) => _hpMultiplierByRegion[IndexOf(region)];

        public static float AttackMultiplierFor(int region) => _attackMultiplierByRegion[IndexOf(region)];

        public static int RequiredQualityTierFor(int region) => _requiredQualityTierByRegion[IndexOf(region)];

        public static Engine.AffixRarity RequiredAffixRarityFor(int region) => _requiredAffixRarityByRegion[IndexOf(region)];

        // Modul: the old flat constants. Kept as the region-1 entries so a
        // caller that has not been taught about regions still gets a sane wall
        // rather than none - but there are no such callers left, and
        // OfflineSimulationEngine (which read these DIRECTLY, and so also
        // skipped First Blood relief entirely) now goes through MaxHpFor and
        // AttackPowerFor like every other site.
        public const int FirstClearHpMultiplier = 3;
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
            => MaxHpFor(defeatedMask, monsterId, firstBloodLevel: 0);

        /// <summary>
        /// The same, softened by First Blood - the Giantslayer bough.
        ///
        /// It reduces the PENALTY, never the boss: at level 8 the 5x multiplier
        /// becomes about 3.4x, and no amount of investment can take it below
        /// 1x. Softening the wall is the reward; removing it would delete the
        /// mechanic the wall exists for.
        /// </summary>
        public static long MaxHpFor(byte defeatedMask, int monsterId, int firstBloodLevel)
        {
            long baseHp = ContentRegistry.GetScaledMonsterMaxHp(monsterId);
            if (!IsFirstClearPending(defeatedMask, monsterId)) return baseHp;

            float penalty = HpMultiplierFor(RegionOfBoss(monsterId)) - 1f;
            float relief = Engine.SkillTreeRegistry.GetBonusPercent(
                Engine.SkillTreeRegistry.BoughFirstBlood, firstBloodLevel) / 100f;
            float softened = 1f + penalty * Math.Max(0f, 1f - relief);

            return (long)Math.Max(baseHp, baseHp * softened);
        }

        public static long AttackPowerFor(byte defeatedMask, int monsterId)
        {
            long baseAttack = ContentRegistry.GetScaledMonsterAttackPower(monsterId);
            return IsFirstClearPending(defeatedMask, monsterId)
                ? (long)(baseAttack * AttackMultiplierFor(RegionOfBoss(monsterId)))
                : baseAttack;
        }
    }
}
