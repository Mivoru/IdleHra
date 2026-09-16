using System;
using System.Collections.Generic;
using System.Numerics;

namespace FolkIdle.Server.Engine
{
    public enum TraitRarity { Common = 0, Rare = 1, Legendary = 2, Flaw = 3 }

    public enum TraitEffect
    {
        MaxHpPct,
        AttackPct,
        GatherSpeedPct,
        GatherYieldPct,
        CritChancePoints,
        AttackSpeedPct,
        DodgePoints,
        LifestealPct,
        RarityElevationPoints,
    }

    public readonly record struct TraitDefinition(
        int Bit, string Key, string Name, TraitRarity Rarity, TraitEffect Effect, int Value, string Description);

    /// <summary>
    /// THE HERITABLE TRAITS. See docs/superpowers/specs/2026-09-13-breeding-traits-design.md.
    ///
    /// Modul: they replace three genes nobody could see - Speed, Crit and Yield
    /// paid hundredths of a percent, a village partner carried zeroes, and a
    /// "mutation" XOR'd the low bits. A trait has a name, a rarity and one
    /// effect the game demonstrably reads.
    ///
    /// THE BIT IS A PERSISTED FORMAT. A character stores its traits as positions
    /// in character_lineage_registry.TraitMask, so a trait may be ADDED on a free
    /// bit and never moved. TraitRegistryTests pins every one. Flaws start at 16
    /// so positive traits have room to grow.
    ///
    /// PURE AND STATIC like BreedingAptitudes: no database, no session.
    /// </summary>
    public static class TraitRegistry
    {
        public const int MaxTraitsPerCharacter = 3;

        public const int StoutHeart = 0;
        public const int KeenEdge = 1;
        public const int QuickHands = 2;
        public const int GreenThumb = 3;
        public const int IronBlood = 4;
        public const int HawkEye = 5;
        public const int SwiftBlood = 6;
        public const int Nimble = 7;
        public const int BloodOfKings = 8;
        public const int WolfsHunger = 9;
        public const int FaeTouched = 10;
        public const int ThinBlood = 16;
        public const int FaintHeart = 17;
        public const int ClumsyHands = 18;

        public static readonly TraitDefinition[] All =
        {
            new(StoutHeart, "stout_heart", "Stout Heart", TraitRarity.Common, TraitEffect.MaxHpPct, 5, "+5% max HP"),
            new(KeenEdge, "keen_edge", "Keen Edge", TraitRarity.Common, TraitEffect.AttackPct, 4, "+4% attack damage"),
            new(QuickHands, "quick_hands", "Quick Hands", TraitRarity.Common, TraitEffect.GatherSpeedPct, 5, "+5% gathering speed"),
            new(GreenThumb, "green_thumb", "Green Thumb", TraitRarity.Common, TraitEffect.GatherYieldPct, 5, "+5% gathering yield"),
            new(IronBlood, "iron_blood", "Iron Blood", TraitRarity.Rare, TraitEffect.MaxHpPct, 8, "+8% max HP"),
            new(HawkEye, "hawk_eye", "Hawk Eye", TraitRarity.Rare, TraitEffect.CritChancePoints, 3, "+3 crit chance"),
            new(SwiftBlood, "swift_blood", "Swift Blood", TraitRarity.Rare, TraitEffect.AttackSpeedPct, 6, "+6% attack speed"),
            new(Nimble, "nimble", "Nimble", TraitRarity.Rare, TraitEffect.DodgePoints, 4, "+4 dodge"),
            new(BloodOfKings, "blood_of_kings", "Blood of Kings", TraitRarity.Legendary, TraitEffect.AttackPct, 10, "+10% attack damage"),
            new(WolfsHunger, "wolfs_hunger", "Wolf's Hunger", TraitRarity.Legendary, TraitEffect.LifestealPct, 3, "+3% lifesteal"),
            new(FaeTouched, "fae_touched", "Fae Touched", TraitRarity.Legendary, TraitEffect.RarityElevationPoints, 4, "+4 chance a drop is a rarity higher"),
            new(ThinBlood, "thin_blood", "Thin Blood", TraitRarity.Flaw, TraitEffect.MaxHpPct, -6, "-6% max HP"),
            new(FaintHeart, "faint_heart", "Faint Heart", TraitRarity.Flaw, TraitEffect.AttackPct, -5, "-5% attack damage"),
            new(ClumsyHands, "clumsy_hands", "Clumsy Hands", TraitRarity.Flaw, TraitEffect.GatherSpeedPct, -6, "-6% gathering speed"),
        };

        /// <summary>Every bit that names a trait. A stray bit from a bad write is ignored, never applied.</summary>
        public static readonly long KnownBitsMask = BuildKnownMask();

        private static long BuildKnownMask()
        {
            long mask = 0L;
            foreach (var t in All) mask |= 1L << t.Bit;
            return mask;
        }

        public static bool TryGet(int bit, out TraitDefinition definition)
        {
            foreach (var t in All)
            {
                if (t.Bit == bit)
                {
                    definition = t;
                    return true;
                }
            }
            definition = default;
            return false;
        }

        public static bool Has(long mask, int bit) => bit is >= 0 and < 63 && (mask & (1L << bit)) != 0;

        public static int CountOf(long mask) => BitOperations.PopCount((ulong)(mask & KnownBitsMask));

        public static IEnumerable<int> BitsOf(long mask)
        {
            long known = mask & KnownBitsMask;
            for (int bit = 0; bit < 63; bit++)
            {
                if ((known & (1L << bit)) != 0) yield return bit;
            }
        }

        public static long MaskOf(params int[] bits)
        {
            long mask = 0L;
            foreach (int bit in bits) mask |= 1L << bit;
            return mask;
        }

        public static IReadOnlyList<TraitDefinition> OfRarity(TraitRarity rarity) => Array.FindAll(All, t => t.Rarity == rarity);

        public static bool IsFlaw(int bit) => TryGet(bit, out var def) && def.Rarity == TraitRarity.Flaw;
    }
}
