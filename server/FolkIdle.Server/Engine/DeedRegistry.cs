using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Everything the Book of Deeds can ask about an account, in one struct.
    ///
    /// A SNAPSHOT RATHER THAN A DATABASE, so every deed below is arithmetic
    /// over numbers and the whole chapter list can be verified without a
    /// Testcontainer. The gathering of it - which tables these come from - is
    /// DeedProgressSource's job and nothing here knows about it.
    /// </summary>
    public readonly record struct DeedContext(
        int Level,
        bool HasWeaponEquipped,
        int LarderStocked,
        long WoodStock,
        long ItemsCrafted,
        long TotalKills,
        long BossesSlain,
        int RegionsCompleted,
        int HighestUnlockedRegion,
        int DefeatedRegionBossMask,
        long ForgeFusions,
        long AffixRerolls,
        int HighestRarityOwned,
        int LargestActiveSetBonus,
        int ForgeLevel,
        int InnLevel,
        int VillageBuildingLevelTotal,
        int WarehouseLevel,
        long GatheringMasteryTotal,
        int LowestRegionOneKillCount,
        int BestCodexRegionCompletion,
        int BestSeasonRank,
        int ChildrenBred,
        int EpicChildrenBred,
        int BestAptitudeTotal);

    /// <summary>
    /// One deed: a thing to do, and a number that says how far along it is.
    /// </summary>
    public sealed record Deed(
        string Id,
        string Title,
        string Body,
        string Screen,
        long Target,
        Func<DeedContext, long> Progress);

    public sealed record DeedChapter(
        int Index,
        string Title,
        string Reward,
        IReadOnlyList<Deed> Deeds);

    /// <summary>
    /// The five chapters, and the two rules they are written under.
    ///
    /// **EVERY DEED SHOWS A LIVE x/y COUNTER.** That is the first
    /// non-negotiable in LONG_GAME_SPEC part 2, and the reason the old tiered
    /// achievements failed: `GetNextTierTarget` returned 0 for most ids and the
    /// client rendered "0 / MAX". A deed without a number does not exist to the
    /// player, so every entry below computes one - binary deeds simply target 1.
    ///
    /// **EVERY THRESHOLD IS CALIBRATED AGAINST MEASURED PACING**, the second
    /// non-negotiable. `ProgressionRateTests` measures one hour of play as 53
    /// kills and 1,529 gold, and reaching region 5 as ~629 geared hours. The old
    /// Treasury tier IV wanted 2.5 BILLION gold - about two full seasons of
    /// uninterrupted region-4 farming spent on nothing else - which is not a
    /// goal, it is a wall with a number painted on it.
    ///
    /// ON THE SERVER, not in the client, because completing a chapter awards a
    /// Seal and **a Seal grants +2 permanent skill points every season,
    /// forever**. A client that decided when it had earned one would be a
    /// client that could award itself the tree.
    ///
    /// THREE SUBSTITUTIONS FROM THE SPEC, all for the same reason - the spec
    /// names a thing this game does not count, and inventing a counter for one
    /// deed is worse than moving the deed:
    ///
    /// - II asks for "craft from 3 different materials"; nothing records which
    ///   materials a craft consumed, so it asks for ten crafts instead.
    /// - III asks to "survive a fight below 10% HP"; nothing observes a narrow
    ///   escape, and detecting one would mean a branch on the 10 Hz combat
    ///   path for a single checklist entry. Reaching region 3 stands in - the
    ///   same chapter's lesson (you are strong enough to travel) with a number.
    /// - IV asks for "cooking totals" and "1M harvests"; cooking is not counted
    ///   separately from crafting and a million harvests is roughly forty
    ///   seasons. Gathering mastery across the four professions stands in for
    ///   both, and it is the number that screen already shows.
    /// </summary>
    public static class DeedRegistry
    {
        public const int ChapterCount = 5;

        /// <summary>Skill points a single Seal is worth, every season.</summary>
        public const int SkillPointsPerSeal = 2;

        public static IReadOnlyList<DeedChapter> Chapters { get; } = Build();

        /// <summary>Whether this chapter's Seal is already held.</summary>
        public static bool HasSeal(int sealsMask, int chapterIndex)
            => (sealsMask & (1 << (chapterIndex - 1))) != 0;

        public static int WithSeal(int sealsMask, int chapterIndex)
            => sealsMask | (1 << (chapterIndex - 1));

        /// <summary>How many Seals a mask holds - and so how many extra skill
        /// points the account starts every season with.</summary>
        public static int SealCount(int sealsMask)
        {
            int count = 0;
            for (int chapter = 1; chapter <= ChapterCount; chapter++)
            {
                if (HasSeal(sealsMask, chapter)) count++;
            }
            return count;
        }

        public static int SkillPointsFrom(int sealsMask) => SealCount(sealsMask) * SkillPointsPerSeal;

        /// <summary>
        /// Whether every deed in a chapter is done.
        ///
        /// A CHAPTER OPENS WHEN THE ONE BEFORE IT COMPLETES, which is the
        /// caller's business; this answers only "is this one finished".
        /// </summary>
        public static bool IsComplete(DeedChapter chapter, DeedContext context)
        {
            for (int i = 0; i < chapter.Deeds.Count; i++)
            {
                if (chapter.Deeds[i].Progress(context) < chapter.Deeds[i].Target) return false;
            }
            return true;
        }

        private static IReadOnlyList<DeedChapter> Build()
        {
            return new List<DeedChapter>
            {
                // I - THE VILLAGE ROAD. This chapter is the interactive
                // tutorial: onboarding expressed as content with rewards
                // instead of popups that get clicked away, and the ORDER is the
                // lesson - fight, wear what drops, eat, gather, make something,
                // keep going. A new player who does these in sequence has
                // touched every loop the game has.
                new DeedChapter(1, "The Village Road", "A Seal, and a set of Common tools", new List<Deed>
                {
                    new("first-blood", "Win your first fight",
                        "Open Combat and send your character at Field Mouse. It keeps fighting on its own, even after you close the page.",
                        "combat", 2, c => Math.Min(c.Level, 2)),
                    new("dress-up", "Wear a weapon",
                        "Monsters drop equipment. Open Character and click the weapon slot - gear is where nearly all of your power comes from, not levels.",
                        "character", 1, c => c.HasWeaponEquipped ? 1 : 0),
                    new("stock-larder", "Fill the larder",
                        "Load food into Auto-Eat. It heals you mid-fight, and without it the fourth monster of a region will kill you.",
                        "larder", 1, c => Math.Min(c.LarderStocked, 1)),
                    new("hundred-logs", "Gather 100 wood",
                        "Open Gathering and set your character to chop. Wood is what the village and half of crafting are built from.",
                        "gathering", 100, c => Math.Min(c.WoodStock, 100)),
                    new("first-craft", "Craft something",
                        "Take your materials to Crafting. Made gear beats found gear at the same level, and it is how you choose what you get.",
                        "crafting", 1, c => Math.Min(c.ItemsCrafted, 1)),
                    new("level-ten", "Reach level 10",
                        "Keep a fight running. Everything above happens once; this one just means you have settled in.",
                        "combat", 10, c => Math.Min(c.Level, 10)),
                }),

                // II - SMITHS. Everything here is the Forge, which is the
                // system a new player is least likely to find on their own and
                // the one that decides whether their gear keeps up.
                new DeedChapter(2, "Smiths", "A Seal", new List<Deed>
                {
                    new("fifty-fusions", "Fuse fifty times",
                        "Two pieces make a better one. The Forge is how a lucky drop becomes a good item.",
                        "forge", 50, c => Math.Min(c.ForgeFusions, 50)),
                    new("rarity-eight", "Own a rarity 8 item",
                        "Fusion raises rarity. Eight is the middle of the fourteen tiers and well inside a season.",
                        "forge", 8, c => Math.Min(c.HighestRarityOwned, 8)),
                    new("twenty-rerolls", "Reroll twenty affixes",
                        "An affix is rerolled until it says what you want. This is where gold goes once gear stops dropping upgrades.",
                        "forge", 20, c => Math.Min(c.AffixRerolls, 20)),
                    new("two-piece", "Wear two pieces of one set",
                        "Matching pieces pay a bonus on top of their own stats.",
                        "character", 2, c => Math.Min(c.LargestActiveSetBonus, 2)),
                    new("forge-five", "Raise the Forge to level 5",
                        "The Forge's level caps the rarity a fusion can reach, which nothing on that screen says out loud.",
                        "village", 5, c => Math.Min(c.ForgeLevel, 5)),
                    new("ten-crafts", "Craft ten items",
                        "Enough to have used more than one recipe, and to have felt the Workshop's rarity roll.",
                        "crafting", 10, c => Math.Min(c.ItemsCrafted, 10)),
                }),

                // III - HUNTERS. The combat chapter, and the one that pushes a
                // player out of region 1 - measured at 180 kills to clear, so
                // 5,000 kills is roughly a hundred hours and lands mid-season.
                new DeedChapter(3, "Hunters", "A Seal", new List<Deed>
                {
                    new("region-one-hundred", "Kill each of region 1's five monsters a hundred times",
                        "The counter shows your WEAKEST of the five, so this finishes when none of them is neglected.",
                        "combat", 100, c => Math.Min(c.LowestRegionOneKillCount, 100)),
                    new("first-boss", "Put down a region boss",
                        "A boss carries five times its health the first time. After that it can be farmed.",
                        "combat", 1, c => c.DefeatedRegionBossMask != 0 ? 1 : 0),
                    new("level-forty", "Reach level 40",
                        "About where region 2 stops being dangerous.",
                        "combat", 40, c => Math.Min(c.Level, 40)),
                    new("five-thousand", "Five thousand kills",
                        "Roughly a hundred hours of fighting at the measured rate. This is the chapter's long one.",
                        "combat", 5000, c => Math.Min(c.TotalKills, 5000)),
                    new("reach-region-three", "Reach region 3",
                        "Two bosses down. The ladder is one continuous curve, so every monster past here is a gear check.",
                        "combat", 3, c => Math.Min(c.HighestUnlockedRegion, 3)),
                    new("codex-region", "Finish one region's codex",
                        "Every monster of one region, recorded. The codex pays a permanent yield and damage bonus for it.",
                        "codex", 1, c => Math.Min(c.BestCodexRegionCompletion, 1)),
                }),

                // IV - STEWARDS. The village and the professions, which are the
                // half of the game a combat-first player never opens.
                new DeedChapter(4, "Stewards", "A Seal", new List<Deed>
                {
                    new("village-twenty", "Twenty levels of village buildings",
                        "Across all of them. The Town Hall caps every other building, so it is where this starts.",
                        "village", 20, c => Math.Min(c.VillageBuildingLevelTotal, 20)),
                    new("warehouse-three", "Raise the Warehouse to level 3",
                        "Storage for what the Lumberjack, Quarry and Mine produce while you are away.",
                        "village", 3, c => Math.Min(c.WarehouseLevel, 3)),
                    new("mastery-hundred", "A hundred levels of gathering mastery",
                        "Across woodcutting, mining, fishing and herbalism together. Each profession has its own track.",
                        "gathering", 100, c => Math.Min(c.GatheringMasteryTotal, 100)),
                    new("inn-five", "Raise the Inn to level 5",
                        "The Inn decides both how often people come to the village and how good their blood is.",
                        "village", 5, c => Math.Min(c.InnLevel, 5)),
                    new("fifty-crafts", "Craft fifty items",
                        "By now the Workshop's rarity roll is a number you have opinions about.",
                        "crafting", 50, c => Math.Min(c.ItemsCrafted, 50)),
                    new("region-complete", "Complete a region",
                        "Every monster in it beaten at least once, boss included.",
                        "codex", 1, c => Math.Min(c.RegionsCompleted, 1)),
                }),

                // V - THE LEDGER OF LEGENDS. The chapter nobody finishes in
                // their first season, and the only one that asks for the two
                // things a season leaves behind.
                new DeedChapter(5, "The Ledger of Legends", "A Seal", new List<Deed>
                {
                    new("top-fifty", "Finish a season in the top fifty",
                        "Ranked by level, then by the hardest monster you ever put down.",
                        "progression", 1, c => c.BestSeasonRank > 0 && c.BestSeasonRank <= 50 ? 1 : 0),
                    new("five-piece", "Wear five pieces of one set",
                        "A full set. The bonus steps at two, three and five.",
                        "character", 5, c => Math.Min(c.LargestActiveSetBonus, 5)),
                    new("level-hundred", "Reach level 100",
                        "Measured at about thirteen hours of geared play across all five regions.",
                        "combat", 100, c => Math.Min(c.Level, 100)),
                    new("last-boss", "Put down Malakor",
                        "Region 5's boss, and the only monster nobody reaches without clearing everything before it.",
                        "combat", 1,
                        c => (c.DefeatedRegionBossMask & (1 << (RaceUnlockRegistry.LastRegion - RaceUnlockRegistry.FirstRegion))) != 0 ? 1 : 0),
                    new("raise-a-child", "Raise a child",
                        "Breed once. A child is the only thing that carries a bloodline into the next season.",
                        "breeding", 1, c => Math.Min(c.ChildrenBred, 1)),
                    new("epic-child", "Breed an epic child",
                        "A 5% roll, +1 to all four aptitudes and a crown on the portrait. This one is luck, and it is meant to be.",
                        "breeding", 1, c => Math.Min(c.EpicChildrenBred, 1)),
                }),
            };
        }
    }
}
