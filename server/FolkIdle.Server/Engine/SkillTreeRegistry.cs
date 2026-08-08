using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// The skill tree: what a level is worth, spent by the player.
    ///
    /// REPLACES THE FOUR ACTIVE SKILLS, which were removed for a measured
    /// reason rather than a taste one. They multiplied the next hit by 150 to
    /// 500 percent on cooldowns of three to twenty seconds, and mana refilled
    /// faster than the cooldowns cleared - so at a 1.5 second swing, 390 of
    /// every 400 swings could be buffed. That is +90% damage, +136% with the
    /// status synergy, available only to a player willing to click every three
    /// seconds. In an idle game that is two different games sharing a balance
    /// sheet, and the pacing model knew about neither.
    ///
    /// The points survive because they were the good part: one per account
    /// level, a steady small decision. What they buy is now passive.
    ///
    /// ACCOUNT LEVEL, NOT CHARACTER LEVEL. The characters table has a Level
    /// column that nothing in the server ever writes during play; the level
    /// that moves is the account's. More to the point, characters in this game
    /// are semi-disposable - they age through phases and are bred and replaced -
    /// so hanging permanent progression on one would be a trap the first time a
    /// player's investment aged out.
    ///
    /// THREE RINGS, AND WHY THE TREE NEEDED THEM
    ///
    /// The first version was five branches of twenty flat levels. That looked
    /// like a choice and was not: every branch was a pure bonus and the cost
    /// curve was nearly flat, so the optimal play was "pour everything into the
    /// strongest, then the next". It was an ORDERING, not an identity, and two
    /// players ended a season looking the same.
    ///
    /// A tree needs somewhere a door closes. So:
    ///
    ///   ROOTS  (ids 0-4)   max 10, 1 point a level. No choice here on
    ///                      purpose - this is the "you want some of each"
    ///                      layer and it should be cheap.
    ///
    ///   BOUGHS (ids 5-14)  max 8, 2 points a level, needs its root at 5.
    ///                      Each root forks into TWO and only ONE may be
    ///                      levelled. Taking one LOCKS THE OTHER for the
    ///                      season. This is where the choice lives.
    ///
    ///   CROWNS (ids 15-19) one level, 12 points, needs its bough at 5. Not a
    ///                      percentage - a qualitative effect.
    ///
    /// Budget: a full limb is 10 + 16 + 12 = 38, five limbs is 190, and a
    /// season pays about 100 (plus 2 per Seal). So a season buys two full limbs
    /// and part of a third, and all five crowns are out of reach. That gap is
    /// the design.
    ///
    /// IDS 0-4 ARE UNCHANGED, deliberately: player_skill_tree rows already
    /// carry them and they still mean the same five things, so no row has to be
    /// rewritten. Only the root CAP moved, 20 to 10, which
    /// SkillTreeEngine.ReconcileRootCapAsync refunds.
    /// </summary>
    public static class SkillTreeRegistry
    {
        // --- roots ---------------------------------------------------------
        public const int BranchLootRarity = 0;
        public const int BranchWorldBossDamage = 1;
        public const int BranchCritChance = 2;
        public const int BranchCritDamage = 3;
        public const int BranchXpGain = 4;

        public const int RootCount = 5;

        // --- boughs: two per root, in root order ----------------------------
        public const int BoughPlenty = 5;          // Fortune A
        public const int BoughRarity = 6;          // Fortune B
        public const int BoughFirstBlood = 7;      // Giantslayer A
        public const int BoughTrophyHunter = 8;    // Giantslayer B
        public const int BoughGuile = 9;           // Precision A
        public const int BoughRelentless = 10;     // Precision B
        public const int BoughBloodthirst = 11;    // Cruelty A
        public const int BoughFortitude = 12;      // Cruelty B
        public const int BoughCraft = 13;          // Insight A
        public const int BoughHarvest = 14;        // Insight B

        public const int FirstBoughId = 5;
        public const int BoughCount = 10;

        // --- crowns: one per root -------------------------------------------
        public const int CrownGoldenFleece = 15;
        public const int CrownThunderer = 16;
        public const int CrownDoubleStrike = 17;
        public const int CrownLastStand = 18;
        public const int CrownScholar = 19;

        public const int FirstCrownId = 15;
        public const int CrownCount = 5;

        /// <summary>Total node ids, and the size of any levels array.</summary>
        public const int NodeCount = 20;

        /// <summary>
        /// Kept as the name the old five-branch code used. It now means the
        /// number of ROOTS, which is what every existing caller meant by it.
        /// </summary>
        public const int BranchCount = RootCount;

        public const int RootMaxLevel = 10;
        public const int BoughMaxLevel = 8;
        public const int CrownMaxLevel = 1;

        /// <summary>
        /// The old flat cap. Only ReconcileRootCapAsync still needs it, to work
        /// out what a player overpaid before the cap moved to 10.
        /// </summary>
        public const int LegacyRootMaxLevel = 20;

        public const int CrownCost = 12;
        public const int BoughCostPerLevel = 2;

        /// <summary>What must be true of the parent before a node opens.</summary>
        public const int BoughRequiresRootLevel = 5;
        public const int CrownRequiresBoughLevel = 5;

        public enum Ring { Root, Bough, Crown }

        public static bool IsValidNode(int nodeId) => nodeId >= 0 && nodeId < NodeCount;

        /// <summary>Kept for the callers that only ever meant a root.</summary>
        public static bool IsValidBranch(int nodeId) => nodeId >= 0 && nodeId < RootCount;

        public static Ring RingOf(int nodeId)
        {
            if (nodeId >= FirstCrownId) return Ring.Crown;
            if (nodeId >= FirstBoughId) return Ring.Bough;
            return Ring.Root;
        }

        /// <summary>The root a node hangs from - itself, for a root.</summary>
        public static int RootOf(int nodeId) => RingOf(nodeId) switch
        {
            Ring.Bough => (nodeId - FirstBoughId) / 2,
            Ring.Crown => nodeId - FirstCrownId,
            _ => nodeId,
        };

        /// <summary>
        /// The other bough on the same fork - the one taking this node locks.
        /// -1 for anything that is not a bough.
        /// </summary>
        public static int SiblingBoughOf(int nodeId)
        {
            if (RingOf(nodeId) != Ring.Bough) return -1;
            int offset = nodeId - FirstBoughId;
            return FirstBoughId + (offset % 2 == 0 ? offset + 1 : offset - 1);
        }

        /// <summary>The two boughs hanging off a root, in id order.</summary>
        public static (int A, int B) BoughsOfRoot(int rootId)
            => (FirstBoughId + rootId * 2, FirstBoughId + rootId * 2 + 1);

        public static int CrownOfRoot(int rootId) => FirstCrownId + rootId;

        /// <summary>The bough a crown sits above.</summary>
        public static int BoughFeedingCrown(int crownId, byte[] levels)
        {
            var (a, b) = BoughsOfRoot(RootOf(crownId));
            // Whichever fork the player actually took. Both cannot be taken.
            return levels[a] > 0 ? a : b;
        }

        public static int MaxLevelOf(int nodeId) => RingOf(nodeId) switch
        {
            Ring.Root => RootMaxLevel,
            Ring.Bough => BoughMaxLevel,
            _ => CrownMaxLevel,
        };

        /// <summary>
        /// Tenths of a percent per level, so a branch can be smaller than one
        /// percent a step without the table growing decimals.
        ///
        /// The sizes are not uniform, and that is the whole design: each node
        /// is scaled to what it actually moves. Roots keep the values they were
        /// balanced at; halving the cap from 20 to 10 halved their ceiling,
        /// which is intended - the ceiling moved into the rings above.
        /// </summary>
        private static readonly int[] TenthsOfPercentPerLevel = new int[NodeCount]
        {
            // --- roots, unchanged per level -------------------------------
            10,   // 0  Fortune       loot rarity, +1.0%/lvl, +10% at cap
            20,   // 1  Giantslayer   world boss damage, +2.0%/lvl, +20%
            4,    // 2  Precision     crit chance, +0.4 POINTS/lvl, +4
            30,   // 3  Cruelty       crit damage, +3.0%/lvl, +30%
            4,    // 4  Insight       xp, +0.4%/lvl, +4% - smallest on purpose,
                  //                  it is the only one that shortens a season

            // --- boughs, 8 levels each ------------------------------------
            15,   // 5  Plenty        +1.5%/lvl material quantity, +12%
            10,   // 6  Rarity        +1.0%/lvl rarity upgrade chance, +8%
            40,   // 7  First Blood   -4.0%/lvl of the first-clear HP penalty,
                  //                  -32%: 5x becomes ~3.4x at cap
            25,   // 8  Trophy Hunter +2.5%/lvl boss gold, +20%
            30,   // 9  Guile         +3.0%/lvl crit damage, +24%
            10,   // 10 Relentless    -1.0%/lvl attack interval, -8%
            5,    // 11 Bloodthirst   +0.5%/lvl of damage as healing, +4%
            20,   // 12 Fortitude     +2.0%/lvl max health and armour, +16%
            25,   // 13 Craft         -2.5%/lvl craft time, -20%
            20,   // 14 Harvest       +2.0%/lvl gathering speed, +16%

            // --- crowns: qualitative, the number is the one knob each has ---
            100,  // 15 Golden Fleece every 100th kill, read as a COUNT
            5000, // 16 Thunderer     opening blow at 500% weapon damage
            250,  // 17 Double Strike 25% chance a crit lands twice
            10,   // 18 Last Stand    one survival per hour (the 1.0 is unused)
            250,  // 19 Scholar       +25% offline rate
        };

        private static readonly string[] Names = new string[NodeCount]
        {
            "Fortune", "Giantslayer", "Precision", "Cruelty", "Insight",
            "Plenty", "Rarity", "First Blood", "Trophy Hunter", "Guile",
            "Relentless", "Bloodthirst", "Fortitude", "Craft", "Harvest",
            "Golden Fleece", "Thunderer", "Double Strike", "Last Stand", "Scholar",
        };

        private static readonly string[] Blurbs = new string[NodeCount]
        {
            "Better rarity on what drops. Not more loot - better loot.",
            "Every blow against a world boss lands harder.",
            "More of your hits are critical ones.",
            "Your critical hits take a larger bite.",
            "Levels arrive sooner, which is the slowest part of a season.",

            "Materials drop in bigger stacks. Crafting eats stacks.",
            "A drop has a chance to roll one rarity higher than it should.",
            "A boss you have never beaten is less monstrous the first time.",
            "Bosses pay more gold, and always leave a material behind.",
            "Critical hits bite deeper still.",
            "You swing faster. Everything else scales off how often you hit.",
            "A share of the damage you deal comes back as health - food you never had to cook.",
            "More health and more armour. The difference between a wall and a grind.",
            "A craft sometimes costs you nothing at all - the materials stay in the sack.",
            "Gathering finishes sooner, and sometimes yields twice.",

            "Every hundredth kill drops an item two rarity tiers above its due.",
            "You open a boss fight with a free blow at five times your weapon.",
            "A critical hit has a chance to land a second time.",
            "Once an hour, the blow that would kill you leaves you at one health.",
            "Everything you earn while away comes in a quarter faster.",
        };

        public static string GetName(int nodeId)
            => IsValidNode(nodeId) ? Names[nodeId] : "Unknown";

        public static string GetBlurb(int nodeId)
            => IsValidNode(nodeId) ? Blurbs[nodeId] : "Unknown";

        /// <summary>The node's total bonus at this level, in tenths of a percent.</summary>
        public static int GetBonusTenthsOfPercent(int nodeId, int level)
        {
            if (!IsValidNode(nodeId) || level <= 0) return 0;

            // A crown is one level and its number is a flat magnitude, not a
            // per-level rate - multiplying it by the level would be a no-op
            // today and a bug the moment a crown gains a second level.
            if (RingOf(nodeId) == Ring.Crown) return TenthsOfPercentPerLevel[nodeId];

            int max = MaxLevelOf(nodeId);
            if (level > max) level = max;
            return TenthsOfPercentPerLevel[nodeId] * level;
        }

        /// <summary>The same, as a percentage, for callers doing float maths.</summary>
        public static float GetBonusPercent(int nodeId, int level)
            => GetBonusTenthsOfPercent(nodeId, level) / 10f;

        /// <summary>
        /// What the NEXT level costs, in skill points, or 0 if the node is
        /// already at its cap.
        ///
        /// Roots keep the rising curve they had, scaled to the new cap: five
        /// levels at 1, five at 2. Rising rather than flat because a flat price
        /// makes the tree a formality - the last level would be as cheap as the
        /// first.
        /// </summary>
        public static int GetUpgradeCost(int nodeId, int currentLevel)
        {
            if (!IsValidNode(nodeId) || currentLevel < 0) return 0;
            if (currentLevel >= MaxLevelOf(nodeId)) return 0;

            return RingOf(nodeId) switch
            {
                Ring.Root => (currentLevel / 5) + 1,
                Ring.Bough => BoughCostPerLevel,
                _ => CrownCost,
            };
        }

        /// <summary>
        /// The old single-argument form, which only ever meant a root. Kept so
        /// the pre-ring callers and their tests still compile and still mean
        /// the same thing.
        /// </summary>
        public static int GetUpgradeCost(int currentLevel)
            => GetUpgradeCost(BranchLootRarity, currentLevel);

        /// <summary>Points to take one node from nothing to its cap.</summary>
        public static int TotalCostForFullNode(int nodeId)
        {
            int total = 0;
            for (int level = 0; level < MaxLevelOf(nodeId); level++)
            {
                total += GetUpgradeCost(nodeId, level);
            }
            return total;
        }

        /// <summary>Kept for callers that meant a root.</summary>
        public static int TotalCostForFullBranch() => TotalCostForFullNode(BranchLootRarity);

        /// <summary>Root, one bough and the crown - one limb taken all the way.</summary>
        public static int TotalCostForFullLimb(int rootId)
            => TotalCostForFullNode(rootId)
             + TotalCostForFullNode(BoughsOfRoot(rootId).A)
             + CrownCost;

        /// <summary>
        /// Why a node cannot be bought right now, or null if it can.
        ///
        /// Returns a REASON rather than a bool because every one of these is
        /// something the player needs told. A tree that greys a node out
        /// without saying why is a tree nobody plans against - and four of the
        /// five reasons here are recoverable.
        /// </summary>
        public static string? BlockedReason(int nodeId, byte[] levels, int availablePoints)
        {
            if (!IsValidNode(nodeId)) return "No such skill.";
            if (levels.Length < NodeCount) return "Skill tree not loaded.";
            if (IsEffectPending(nodeId)) return "Not in the game yet - coming soon.";

            int level = levels[nodeId];
            if (level >= MaxLevelOf(nodeId)) return "Already at its limit.";

            Ring ring = RingOf(nodeId);
            int rootId = RootOf(nodeId);

            if (ring == Ring.Bough)
            {
                if (levels[rootId] < BoughRequiresRootLevel)
                {
                    return $"Needs {GetName(rootId)} at {BoughRequiresRootLevel}.";
                }

                int sibling = SiblingBoughOf(nodeId);
                if (levels[sibling] > 0)
                {
                    return $"{GetName(sibling)} was taken instead. One branch per fork.";
                }
            }
            else if (ring == Ring.Crown)
            {
                var (a, b) = BoughsOfRoot(rootId);
                int taken = Math.Max(levels[a], levels[b]);
                if (taken < CrownRequiresBoughLevel)
                {
                    return $"Needs a branch of {GetName(rootId)} at {CrownRequiresBoughLevel}.";
                }
            }

            int cost = GetUpgradeCost(nodeId, level);
            if (availablePoints < cost)
            {
                return $"Costs {cost} points; you have {availablePoints}.";
            }

            return null;
        }

        /// <summary>
        /// Nodes whose EFFECT is not wired up yet.
        ///
        /// This exists because of a defect class this codebase has shipped
        /// more than once: an output side that was never connected, so a
        /// player spends a real resource on a bonus that quietly does nothing
        /// and has no way to tell. Crafting granted nothing, loot went dead
        /// after twenty kills, gather-speed affixes were computed and never
        /// read - all of them looked finished.
        ///
        /// So an unwired node says so on its own card and cannot be bought.
        /// Delete an entry here in the same commit that wires its effect,
        /// never before.
        /// </summary>
        private static readonly bool[] EffectPending = BuildEffectPending();

        private static bool[] BuildEffectPending()
        {
            var pending = new bool[NodeCount];
            foreach (int id in new[]
            {
                BoughPlenty,
                CrownGoldenFleece, CrownThunderer,
            })
            {
                pending[id] = true;
            }
            return pending;
        }

        public static bool IsEffectPending(int nodeId)
            => IsValidNode(nodeId) && EffectPending[nodeId];

        public static bool CanPurchase(int nodeId, byte[] levels, int availablePoints)
            => BlockedReason(nodeId, levels, availablePoints) == null;
    }
}
