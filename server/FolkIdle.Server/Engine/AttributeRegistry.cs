using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// WHAT THE FOUR ATTRIBUTES ARE, 2026-09-06.
    ///
    /// They became a player's choice earlier today (a level pays points and the
    /// player places them) and that immediately exposed how thin they were:
    ///
    ///   STR  +2 melee damage, +1 armour penetration     - and PENETRATION WAS DEAD
    ///   DEX  +2 ranged damage, speed, crit, accuracy    - and RANGED DAMAGE WAS DEAD
    ///   CON  +15 hp, +1 armour, regen, block            - all four worked
    ///   LCK  +0.05% forge, +0.1% loot                   - two, both minor
    ///
    /// `CombatDamageModel.Mitigate` never took a penetration term, so STR's
    /// second effect and every `armor_pen_flat` affix ever rolled did nothing -
    /// the live account is carrying 1,122 of it on its weapon. And nothing in
    /// combat reads `FlatRangedDamage`; the model has one damage number, so
    /// DEX's headline effect was worth zero while STR's identical one was worth
    /// everything.
    ///
    /// Fixing those two is what makes an allocation screen honest. This file is
    /// the rest of the answer: an identity per attribute, a curve that stops any
    /// one of them running away, and a milestone track that gives the choice a
    /// shape beyond "more of the same".
    ///
    /// THE IDENTITIES
    ///
    ///   Might     hits hard and ignores armour      (damage, penetration)
    ///   Finesse   hits often and precisely          (accuracy, crit, speed)
    ///   Vigour    survives being hit                (health, armour, block)
    ///   Fortune   takes more from the world         (loot, forge, and the
    ///                                                milestones that reach the
    ///                                                economy)
    ///
    /// DEX no longer grants damage at all - that was the dead ranged number -
    /// which is what finally separates it from STR instead of making it a
    /// strictly better version of it.
    ///
    /// EVERY MILESTONE LANDS ON A FIELD THAT ALREADY HAS A READER. That is a
    /// deliberate constraint rather than a coincidence: this codebase's most
    /// expensive recurring defect is a stat that is computed and never consumed,
    /// and a milestone table inventing five new mechanics would be five new
    /// chances at it. Every entry below routes into CombatStats through a field
    /// the live tick already reads.
    /// </summary>
    public static class AttributeRegistry
    {
        public const int Might = 0;
        public const int Finesse = 1;
        public const int Vigour = 2;
        public const int Fortune = 3;
        public const int Count = 4;

        /// <summary>
        /// The value each attribute starts at: ZERO.
        ///
        /// Modul: I ASSUMED 50/50/50/25 AND IT WAS WRONG. PlayerRecord's four
        /// Base* columns are plain ints with no initialiser, so a registration
        /// gets zeroes - the live database confirms it, with three accounts at
        /// 0 and only the oldest one holding 50/50/50/25 from some earlier
        /// scheme. Reading a starting value off one legacy row is how that got
        /// in, and the equipment gate built on top of it would have refused a
        /// brand-new player their first weapon.
        ///
        /// Everything a character has is placed. That is the whole point of the
        /// system, and it is also the honest floor for "spent" when a respec
        /// hands it all back.
        /// </summary>
        public static int StartingValue(int attributeId) => 0;

        public static string NameOf(int attributeId) => attributeId switch
        {
            Might => "Might",
            Finesse => "Finesse",
            Vigour => "Vigour",
            _ => "Fortune",
        };

        /// <summary>
        /// What a milestone does, expressed as which CombatStats field it moves.
        /// Named rather than a raw delta so the client can render the track
        /// without a second copy of the numbers.
        /// </summary>
        public enum MilestoneEffect
        {
            AttackPowerPct,
            ArmourPenetrationFlat,
            AttackSpeedPct,
            AccuracyFlat,
            CritDamagePct,
            CritChancePct,
            MaxHpPct,
            ArmourPct,
            RegenPerSecond,
            CritMitigationPct,
            LootLuckPct,
            GatheringYieldPct,
            GoldPct,
            ForgeSuccessPct,
        }

        public readonly struct Milestone
        {
            public readonly int Attribute;
            public readonly int Threshold;
            public readonly string Name;
            public readonly MilestoneEffect Effect;
            public readonly float Magnitude;

            public Milestone(int attribute, int threshold, string name, MilestoneEffect effect, float magnitude)
            {
                Attribute = attribute;
                Threshold = threshold;
                Name = name;
                Effect = effect;
                Magnitude = magnitude;
            }
        }

        /// <summary>
        /// Five rungs per attribute, at the same thresholds for each, so the
        /// tracks are comparable at a glance and a player can read "I am two
        /// rungs up Might" without arithmetic.
        ///
        /// The thresholds rise steeply on purpose - 25 is an early reward that
        /// arrives inside the first hours, 300 is a season's commitment to one
        /// attribute. A level pays 7 points, so 300 in one attribute is roughly
        /// level 44 spent entirely on it, or level 175 spread evenly.
        /// </summary>
        public static readonly int[] Thresholds = { 25, 60, 120, 200, 300 };

        public static readonly Milestone[] Milestones =
        {
            // MIGHT - the blow itself, and getting it through armour.
            new(Might, 25,  "Heavy Hands",   MilestoneEffect.AttackPowerPct, 5f),
            new(Might, 60,  "Sunder",        MilestoneEffect.ArmourPenetrationFlat, 40f),
            new(Might, 120, "Executioner",   MilestoneEffect.AttackPowerPct, 8f),
            new(Might, 200, "Titan's Grip",  MilestoneEffect.ArmourPenetrationFlat, 80f),
            new(Might, 300, "Worldbreaker",  MilestoneEffect.AttackPowerPct, 12f),

            // FINESSE - landing it, and landing it well.
            new(Finesse, 25,  "Quick Step",        MilestoneEffect.AttackSpeedPct, 3f),
            new(Finesse, 60,  "Keen Eye",          MilestoneEffect.AccuracyFlat, 25f),
            new(Finesse, 120, "Deadly Precision",  MilestoneEffect.CritDamagePct, 15f),
            new(Finesse, 200, "Flurry",            MilestoneEffect.AttackSpeedPct, 4f),
            new(Finesse, 300, "Perfect Form",      MilestoneEffect.CritDamagePct, 25f),

            // VIGOUR - the bar, and what gets through to it.
            new(Vigour, 25,  "Hardy",        MilestoneEffect.MaxHpPct, 5f),
            new(Vigour, 60,  "Thick Skin",   MilestoneEffect.ArmourPct, 10f),
            new(Vigour, 120, "Second Wind",  MilestoneEffect.RegenPerSecond, 2f),
            new(Vigour, 200, "Ironhide",     MilestoneEffect.MaxHpPct, 8f),
            new(Vigour, 300, "Unbreakable",  MilestoneEffect.CritMitigationPct, 25f),

            // FORTUNE - the only track that reaches outside a fight, which is
            // what stops it being the dump stat it has always been.
            new(Fortune, 25,  "Scavenger",         MilestoneEffect.LootLuckPct, 8f),
            new(Fortune, 60,  "Prospector",        MilestoneEffect.GatheringYieldPct, 5f),
            new(Fortune, 120, "Lucky Strike",      MilestoneEffect.CritChancePct, 2f),
            new(Fortune, 200, "Golden Touch",      MilestoneEffect.GoldPct, 8f),
            new(Fortune, 300, "Fortune's Favour",  MilestoneEffect.ForgeSuccessPct, 8f),  // 8% off fusion fees
        };

        /// <summary>How many rungs of an attribute's track a value has reached.</summary>
        public static int MilestonesReached(int value)
        {
            int reached = 0;
            for (int i = 0; i < Thresholds.Length; i++)
            {
                if (value >= Thresholds[i]) reached++;
            }
            return reached;
        }

        /// <summary>
        /// The percentage effects, on a square root rather than a straight line.
        ///
        /// Modul: A LEVEL PAYS 7 POINTS AND NOTHING SPENDS THEM FOR YOU, so a
        /// long-played character holds hundreds. At the old flat rates that is
        /// +59% crit chance and +29% attack speed from one attribute - the exact
        /// linear-and-uncapped shape that PowerCeilingTests exists to refuse,
        /// and it would have made Finesse the only correct answer.
        ///
        /// Square root, matching the gathering mastery and codex damage curves
        /// fixed the same day. The first points are worth MORE than they were
        /// and the top is bounded in practice:
        ///
        ///   points          25     50    100    300    600
        ///   crit, old      2.5%   5.0%  10.0%  30.0%  60.0%
        ///   crit, new      7.5%  10.6%  15.0%  26.0%  36.7%
        ///
        /// The FLAT effects stay linear - health, attack power, armour and
        /// accuracy are all racing content that grows geometrically, so a curve
        /// there would make them stop mattering rather than stop running away.
        /// </summary>
        public const float CritChancePerRootPoint = 1.5f;
        public const float AttackSpeedPerRootPoint = 0.8f;
        public const float BlockStrengthPerRootPoint = 0.6f;
        public const float LootLuckPerRootPoint = 1.2f;
        /// <summary>
        /// Fortune's second effect, replacing forge success. 0.35 per root
        /// point is 1.75% at 25 and 8.6% at 600 - rare enough that an elevated
        /// drop stays an event, common enough to be worth building toward.
        /// </summary>
        public const float RarityElevationPerRootPoint = 0.35f;

        /// <summary>
        /// Kept for the capstone milestone only. Fusion cannot fail, so this is
        /// a discount on the fusion FEE - see StatsCalculator.RarityElevationPct
        /// for why it stopped being a per-point effect.
        /// </summary>
        public const float ForgeSuccessPerRootPoint = 0.6f;

        public static float DiminishedPercent(float perRootPoint, int value)
            => value <= 0 ? 0f : perRootPoint * (float)Math.Sqrt(value);
    }
}
