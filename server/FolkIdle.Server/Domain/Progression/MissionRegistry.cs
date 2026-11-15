using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// One thing a player did that a daily mission can count. The numbers are
    /// stored in PlayerMissionCounters.Action and PlayerMissionDays.Actions, so
    /// a value is never reused for a different act.
    /// </summary>
    public enum MissionAction : short
    {
        // Fixed - offered every day the player has them open.
        DungeonFight = 1,
        ExpeditionSent = 2,
        WorldBossStrike = 3,

        // Rotating - the owner's list (2026-11-15).
        AffixReforge = 10,
        AffixRefine = 11,
        ItemFused = 12,
        ChildBorn = 13,
        FeastThrown = 14,
        DelveDeep = 15,
        BossAscension = 16,
        GuildDonation = 17,
        ChatMessage = 18,
        ToolCrafted = 19,
        PotionCrafted = 20,
        MarketListed = 21,
        AttributePointSpent = 22,
        SkillPointSpent = 23,
        WardrobeChestOpened = 24,
        LootChestOpened = 25,
    }

    /// <summary>
    /// What a player has open, read once when the day's missions are drawn. A
    /// mission whose predicate is false is never offered, because an impossible
    /// mission is the new-player trap the dev fixture can never show.
    /// </summary>
    public sealed record MissionFacts
    {
        public int Level { get; init; }
        public long Gold { get; init; }
        public bool HasGuild { get; init; }
        public int BreedingGroundsLevel { get; init; }
        public bool HasExpeditionCandidate { get; init; }
        public bool FeastPossible { get; init; }
        public int UnspentAttributePoints { get; init; }
        public int AvailableSkillPoints { get; init; }
        public int LootChests { get; init; }
        public int WardrobeChests { get; init; }
        public byte DefeatedRegionBossMask { get; init; }
        public bool DungeonsOpen { get; init; }
        public bool ReachedDeep { get; init; }
        public bool DeepEnabled { get; init; }
        public bool WorldBossActive { get; init; }
    }

    public sealed record MissionDefinition(
        MissionAction Action,
        string Title,
        string Hint,
        int Target,
        bool IsFixed,
        // True when progress is the number of different Details seen (one per
        // dungeon gate) rather than the sum of the counts.
        bool CountsDistinctDetails,
        Func<MissionFacts, bool> Eligible);

    public static class MissionRegistry
    {
        /// <summary>Missions a day, when the player has enough of the game open.</summary>
        public const int MissionsPerDay = 6;

        /// <summary>Chronicle XP for one finished mission (plan: 6 x 30 + 20 = 200 a day).</summary>
        public const int XpPerMission = 30;

        /// <summary>Chronicle XP for finishing every mission of the day.</summary>
        public const int XpAllDoneBonus = 20;

        /// <summary>
        /// Free swaps of a ROTATING mission per day (owner, 2026-11-15: "one or
        /// two a day, so players cannot swap forever down to the easiest").
        /// </summary>
        public const int FreeRerollsPerDay = 2;

        /// <summary>The day the missions shipped (2026-11-15): acts earlier that day were never counted.</summary>
        public const int MissionsLaunchDayKey = 20772;

        /// <summary>
        /// A FIXED mission may be replaced only when it cannot be finished
        /// (owner, 2026-11-15): the world boss is not up this week any more, or
        /// the dungeons on the missions' launch day. Such a replacement does
        /// not use a free swap.
        /// </summary>
        public static bool FixedMissionImpossible(MissionAction action, int dayKey, bool worldBossActive) => action switch
        {
            MissionAction.WorldBossStrike => !worldBossActive,
            MissionAction.DungeonFight => dayKey == MissionsLaunchDayKey,
            _ => false,
        };

        // Modul: FORGE_OPEN_LEVEL and MARKET_OPEN_LEVEL in client_web
        // unlocks.ts. The client greys those screens below these levels, so a
        // mission sending a player there would point at a locked door.
        public const int ForgeOpenLevel = 5;
        public const int MarketOpenLevel = 10;

        /// <summary>DelveRegistry's cheapest entry fee - client unlocks.ts DELVE_FIRST_ENTRY_GOLD.</summary>
        public const long DelveFirstEntryGold = 7_000;

        public static readonly IReadOnlyList<MissionDefinition> All = new MissionDefinition[]
        {
            // Fixed. "Each dungeon once" counts distinct gates, so three runs
            // at one gate do not finish it.
            new(MissionAction.DungeonFight, "Fight in every dungeon", "One run at each of the three gates.", 3, true, true, f => f.DungeonsOpen),
            new(MissionAction.ExpeditionSent, "Send an expedition", "Send a grown villager out from the Breeding Grounds.", 1, true, false, f => f.BreedingGroundsLevel >= 1 && f.HasExpeditionCandidate),
            new(MissionAction.WorldBossStrike, "Strike the world boss", "Any strike at this week's world boss.", 1, true, false, f => f.WorldBossActive),

            // Rotating.
            new(MissionAction.AffixReforge, "Reforge affixes", "Reroll an affix at the Forge three times.", 3, false, false, f => f.Level >= ForgeOpenLevel),
            new(MissionAction.AffixRefine, "Refine affixes", "Refine an affix at the Forge three times.", 3, false, false, f => f.Level >= ForgeOpenLevel),
            new(MissionAction.ItemFused, "Fuse an item", "Fuse a piece up a tier at the Forge.", 1, false, false, f => f.Level >= ForgeOpenLevel),
            new(MissionAction.ChildBorn, "Raise a new generation", "Have a child born at the Breeding Grounds.", 1, false, false, f => f.BreedingGroundsLevel >= 1),
            new(MissionAction.FeastThrown, "Throw a feast", "Throw a feast in the village to draw a newcomer.", 1, false, false, f => f.FeastPossible),
            new(MissionAction.DelveDeep, "Reach the Deep", "Descend past the Delve's eighth floor into the Deep.", 1, false, false, f => f.DeepEnabled && f.ReachedDeep && f.Gold >= DelveFirstEntryGold),
            new(MissionAction.BossAscension, "Climb an ascension", "Fight the highest ascension of a regional boss you have open.", 1, false, false, f => f.DefeatedRegionBossMask != 0),
            new(MissionAction.GuildDonation, "Give to your guild", "Donate gold, gear or materials to your guild.", 1, false, false, f => f.HasGuild),
            new(MissionAction.ChatMessage, "Say hello", "Send a message in chat.", 1, false, false, _ => true),
            new(MissionAction.ToolCrafted, "Craft a tool", "Craft an axe, a pickaxe or a rod.", 1, false, false, f => f.Level >= ForgeOpenLevel),
            new(MissionAction.PotionCrafted, "Brew a potion", "Brew any potion at the bench.", 1, false, false, f => f.Level >= MarketOpenLevel),
            new(MissionAction.MarketListed, "Trade at the market", "List an item on the market.", 1, false, false, f => f.Level >= MarketOpenLevel),
            new(MissionAction.AttributePointSpent, "Train an attribute", "Spend an attribute point.", 1, false, false, f => f.UnspentAttributePoints > 0),
            new(MissionAction.SkillPointSpent, "Learn a skill", "Spend a skill point in the skill tree.", 1, false, false, f => f.AvailableSkillPoints > 0),
            new(MissionAction.WardrobeChestOpened, "Open a wardrobe chest", "Open a cosmetic chest in the Wardrobe.", 1, false, false, f => f.WardrobeChests > 0),
            new(MissionAction.LootChestOpened, "Open a loot chest", "Open a loot chest.", 1, false, false, f => f.LootChests > 0),
        };

        private static readonly Dictionary<MissionAction, MissionDefinition> ByAction = All.ToDictionary(d => d.Action);

        public static bool TryGet(MissionAction action, out MissionDefinition definition)
            => ByAction.TryGetValue(action, out definition!);

        /// <summary>
        /// The day's missions: every eligible fixed one, then rotating ones in an
        /// order seeded by (player, day), up to <see cref="MissionsPerDay"/>.
        /// Pure, so the same facts always give the same day.
        /// </summary>
        public static MissionAction[] Draw(long playerId, long dayKey, MissionFacts facts)
        {
            var picked = All.Where(d => d.IsFixed && d.Eligible(facts)).Select(d => d.Action).ToList();
            foreach (var action in RotatingOrder(playerId, dayKey, 0))
            {
                if (picked.Count >= MissionsPerDay) break;
                if (ByAction[action].Eligible(facts)) picked.Add(action);
            }
            return picked.ToArray();
        }

        /// <summary>
        /// The rotating mission that replaces <paramref name="current"/>, or null
        /// when no eligible rotating mission is left that the day does not hold.
        /// </summary>
        public static MissionAction? RerollPick(long playerId, long dayKey, int rerollNumber, IReadOnlyCollection<MissionAction> today, MissionFacts facts)
        {
            foreach (var action in RotatingOrder(playerId, dayKey, rerollNumber))
            {
                if (today.Contains(action)) continue;
                if (ByAction[action].Eligible(facts)) return action;
            }
            return null;
        }

        // A Fisher-Yates over the rotating pool, seeded per (player, day, salt).
        // Not Random.Shared: a retried request must draw the same day.
        private static IEnumerable<MissionAction> RotatingOrder(long playerId, long dayKey, int salt)
        {
            var pool = All.Where(d => !d.IsFixed).Select(d => d.Action).ToArray();
            ulong state = Mix((ulong)playerId * 0x9E3779B97F4A7C15UL ^ (ulong)dayKey * 0xC2B2AE3D27D4EB4FUL ^ (ulong)(salt + 1) * 0x165667B19E3779F9UL);
            for (int i = pool.Length - 1; i > 0; i--)
            {
                state = Mix(state + 0x9E3779B97F4A7C15UL);
                int j = (int)(state % (ulong)(i + 1));
                (pool[i], pool[j]) = (pool[j], pool[i]);
            }
            return pool;
        }

        // SplitMix64's finaliser.
        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }
}
