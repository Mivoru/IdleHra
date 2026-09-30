using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    public enum CosmeticKind : byte
    {
        Chest = 0,
        Avatar = 1,
        Frame = 2,
    }

    public enum CosmeticSource : byte
    {
        Kill = 0,
        Level = 1,
        Market = 2,
        /// <summary>Task 55: a boss challenge's reward.</summary>
        Challenge = 3,
        /// <summary>Task 87: a Boss Ascension frame.</summary>
        Ascension = 4,
        /// <summary>Task 84: a completed Great Work's frame.</summary>
        GreatWork = 5,
        Dev = 9,
    }

    /// <param name="Art">For an avatar, the monster whose portrait it is (the
    /// client maps the name to a picture). Null for frames, which the client
    /// draws itself.</param>
    /// <param name="Bound">Task 87: earned, not found - an Ascension frame. Never in
    /// a chest's pool and never listable on the market, because a cosmetic that
    /// sells for gold would make a reward "cosmetics only" in name.</param>
    public sealed record CosmeticDefinition(string Id, CosmeticKind Kind, int Rarity, string Name, string? Art, bool Bound = false);

    /// <summary>
    /// Task 54: what cosmetics exist, how rare each is, and the two rolls that
    /// hand out chests. Owner decisions are in
    /// docs/superpowers/plans/2026-09-28-task-54-cosmetics.md.
    ///
    /// Modul: KEYED BY ID STRING, NEVER BY POSITION. A row stores
    /// `avatar_malakor`, not "avatar 25", for the reason TitleRegistry gives:
    /// an index on the wire or in a table is a second copy of an ordering.
    /// Nothing here adds power, which is the whole point - PowerCeilingTests
    /// has no lever to add.
    /// </summary>
    public static class CosmeticRegistry
    {
        public const int Common = 1;
        public const int Rare = 2;
        public const int Epic = 3;
        public const int Legendary = 4;
        public const int MaxRarity = Legendary;

        public static readonly string[] RarityNames = { "", "Common", "Rare", "Epic", "Legendary" };

        public static string ChestId(int rarity) => "chest_" + RarityNames[rarity].ToLowerInvariant();

        // Modul: THE AVATAR POOLS are the 25 canonical monster portraits, rarer
        // the further into the world the monster lives; the bosses of the
        // three hardest regions are the only Legendary faces. Region 1 and 2's
        // bosses sit in Epic beside region 5's regulars, because a boss is a
        // boss but region 1's is also the first one every player kills.
        private static readonly (int Rarity, string[] Monsters)[] AvatarPools =
        {
            (Common, new[] { "Field Mouse", "Horned Rabbit", "Meadow Viper", "Wild Boar",
                             "Thorny Vine", "Gray Direwolf", "Forest Dryad", "Mountain Bear" }),
            (Rare, new[] { "Desert Crab", "Ashen Basilisk", "Ember Elemental", "Sandstone Golem",
                           "Ice Bat", "Snowy Yeti", "Glacial Wraith", "Rock Giant" }),
            (Epic, new[] { "Grave Ghoul", "Fortress Gargoyle", "Dark Necromancer", "Death Knight",
                           "Alpha Wolf", "Shadow Lynx" }),
            (Legendary, new[] { "Magma Wyrm", "Frost Titan", "Malakor" }),
        };

        private static readonly (int Rarity, string[] Names)[] FramePools =
        {
            (Common, new[] { "Oak Band", "Iron Band", "Rope Knot", "Birch Ring" }),
            (Rare, new[] { "Studded Bronze", "Silver Rivets", "Amber Ring", "River Stone" }),
            (Epic, new[] { "Knotwork", "Wyrm Coil", "Frost Rune", "Ember Rune" }),
            (Legendary, new[] { "Sun Crown", "Moon Crown", "Storm Crown", "Worldtree" }),
        };

        public static readonly IReadOnlyList<CosmeticDefinition> All = Build();

        private static readonly Dictionary<string, CosmeticDefinition> ById =
            All.ToDictionary(d => d.Id, StringComparer.Ordinal);

        private static IReadOnlyList<CosmeticDefinition> Build()
        {
            var list = new List<CosmeticDefinition>();
            for (int r = Common; r <= Legendary; r++)
            {
                list.Add(new CosmeticDefinition(ChestId(r), CosmeticKind.Chest, r, RarityNames[r] + " Chest", null));
            }
            foreach (var (rarity, monsters) in AvatarPools)
            {
                foreach (string monster in monsters)
                {
                    list.Add(new CosmeticDefinition("avatar_" + Slug(monster), CosmeticKind.Avatar, rarity, monster, monster));
                }
            }
            foreach (var (rarity, names) in FramePools)
            {
                foreach (string name in names)
                {
                    list.Add(new CosmeticDefinition("frame_" + Slug(name), CosmeticKind.Frame, rarity, name, null));
                }
            }
            // Task 87: the Boss Ascension frames, bound to whoever earned them.
            for (int region = Combat.BossAscensionRegistry.FirstRegion; region <= Combat.BossAscensionRegistry.LastRegion; region++)
            {
                foreach (int step in Combat.BossAscensionRegistry.FrameSteps)
                {
                    list.Add(new CosmeticDefinition(
                        Combat.BossAscensionRegistry.FrameId(region, step), CosmeticKind.Frame,
                        step >= Combat.BossAscensionRegistry.MaxStep ? Legendary : Epic,
                        Combat.BossAscensionRegistry.FrameName(region, step), null, Bound: true));
                }
            }
            // Task 84: a completed Great Work's frame, bound like the Ascension ones.
            for (int region = GreatWorksRegistry.FirstRegion; region <= GreatWorksRegistry.LastRegion; region++)
            {
                list.Add(new CosmeticDefinition(
                    GreatWorksRegistry.FrameId(region), CosmeticKind.Frame, Legendary,
                    GreatWorksRegistry.FrameName(region), null, Bound: true));
            }
            return list;
        }

        private static string Slug(string name) => name.ToLowerInvariant().Replace(' ', '_');

        public static CosmeticDefinition? Find(string? id)
            => id != null && ById.TryGetValue(id, out var def) ? def : null;

        /// <summary>What a chest of this rarity can contain: its avatars and frames.</summary>
        public static IReadOnlyList<CosmeticDefinition> ChestPool(int rarity)
            => All.Where(d => d.Rarity == rarity && d.Kind != CosmeticKind.Chest && !d.Bound).ToList();

        public static CosmeticDefinition PickFromChest(int rarity, Random random)
        {
            var pool = ChestPool(rarity);
            return pool[random.Next(pool.Count)];
        }

        // Modul: MONSTER CHEST RATES (owner, 2026-09-28): "as rare as
        // Mythic / Relic / Ancient / Divine". Each chest rarity drops exactly
        // as often as an equipment piece of its twin tier would -
        // EquipmentDropChance times that tier's share of all drops at zero luck -
        // so a retune of either moves this table with it. LUCK DOES NOT
        // SCALE THESE: a cosmetic economy should not inflate as Fortune grows.
        public static readonly int[] TwinItemTier =
        {
            0, RarityTier.Mythic, RarityTier.Relic, RarityTier.Ancient, RarityTier.Divine,
        };

        public static double ChestChancePerKill(int rarity)
            => CombatLootEngine.EquipmentDropChance * RarityTier.BaseShare(TwinItemTier[rarity]);

        /// <summary>
        /// One roll per kill against the cumulative ladder, rarest first, so a
        /// kill drops at most one chest. Returns the chest rarity, or 0.
        /// </summary>
        public static int RollMonsterChest(double roll)
        {
            double threshold = 0.0;
            for (int r = Legendary; r >= Common; r--)
            {
                threshold += ChestChancePerKill(r);
                if (roll < threshold) return r;
            }
            return 0;
        }

        // Modul: THE LEVEL CHEST (owner, 2026-09-28): one every fifth level,
        // 50 / 30 / 15 / 5.
        public const int LevelsPerChest = 5;
        private static readonly int[] LevelChestPercent = { 0, 50, 30, 15, 5 };

        public static int RollLevelChest(double roll)
        {
            double threshold = 0.0;
            for (int r = Common; r <= Legendary; r++)
            {
                threshold += LevelChestPercent[r] / 100.0;
                if (roll < threshold) return r;
            }
            return Common;
        }

        public static int LevelChestsOwed(int level) => Math.Max(0, level) / LevelsPerChest;

        // Modul: THE SELLER CHOOSES THE PRICE (owner, 2026-09-28) - no corridor,
        // unlike equipment. These are only the arithmetic's own fences.
        public const long MinMarketPrice = 1;
        public const long MaxMarketPrice = 1_000_000_000;
    }
}
