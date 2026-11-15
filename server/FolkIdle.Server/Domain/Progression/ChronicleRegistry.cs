using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Domain.Economy;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>One Chronicle season's identity: its name and its three keepsakes.</summary>
    /// <param name="Number">1-based, counted from <see cref="ChronicleRegistry.FirstSeasonDayKey"/>.</param>
    /// <param name="AvatarArt">Sprite path under /sprites/, or null while the season has no painting yet.</param>
    /// <param name="PetArt">Sprite path under /sprites/, or null while the season has no painting yet.</param>
    public sealed record ChronicleSeason(
        int Number,
        string Name,
        string AvatarId, string AvatarName, string? AvatarArt,
        string FrameId, string FrameName,
        string PetId, string PetName, PetStat PetStat, string? PetArt)
    {
        /// <summary>Both paintings exist, so the avatar and the pet are real rewards.</summary>
        public bool HasArt => AvatarArt != null && PetArt != null;
    }

    public enum ChronicleRewardKind : byte
    {
        Gold = 1,
        Diamonds = 2,
        Tickets = 3,
        LootChest = 4,
        WardrobeChest = 5,
        Avatar = 6,
        Frame = 7,
        Pet = 8,
    }

    /// <summary>
    /// One line of a level's reward. Amount is minutes of income for Gold, a
    /// count for Diamonds and Tickets (per gate), the LootChestTier for a loot
    /// chest, and the cosmetic rarity for a wardrobe chest.
    /// </summary>
    public sealed record ChronicleReward(ChronicleRewardKind Kind, int Amount);

    /// <summary>
    /// The Chronicle pass (owner, 2026-11-15; docs/design/CHRONICLE_PASS.md
    /// step 3): free only, 30-day seasons, 50 levels of 100 XP, levelled by the
    /// daily missions' XP.
    /// </summary>
    /// <remarks>
    /// Modul: THE REWARD TABLE IS THE OWNER'S, approved 2026-11-15 as proposed:
    /// each block of five levels pays 60 min of gold, a ticket at every gate,
    /// 15 diamonds, 90 min of gold and a chest. On top: +25 diamonds at
    /// 10/20/30/45, and the avatar (25) and frame (40) carry +25 each. Totals a
    /// season: 300 diamonds (the Delve pays at most 60 a week), 25 hours of the
    /// player's own gold income, 3 Gold + 2 Ancient loot chests, 2 Legendary
    /// wardrobe chests, 10 ticket sets, and the season pet at 50.
    ///
    /// Gold is MINUTES OF INCOME (GoldRewardScale), the same rule the login week
    /// uses, because the three live players earn 10M-84M gold an hour and any
    /// fixed figure would be noise to one of them.
    /// </remarks>
    public static class ChronicleRegistry
    {
        public const int Levels = 50;
        public const int XpPerLevel = 100;
        public const int SeasonDays = 30;
        public const int MaxXpForLevels = Levels * XpPerLevel;

        /// <summary>Past level 50, every this many XP pays one bonus chest.</summary>
        public const int OverflowXpPerChest = 100;
        public const LootChestTier OverflowChest = LootChestTier.Gold;

        /// <summary>
        /// Season 1 opens on 2026-11-15 (UTC day 20772), the day the daily
        /// missions shipped, so the XP banked on day one counts.
        /// </summary>
        public const int FirstSeasonDayKey = 20772;

        // Modul: SEASON NAMES ARE CELTIC/SLAVIC FOLKLORE (owner, 2026-11-15),
        // NOT CZECH MONTHS - those were rejected. They cycle after twelve.
        public static readonly string[] SeasonNames =
        {
            "Veles' Chronicle", "Morana's Chronicle", "The Wild Hunt", "Kupala Night",
            "Svarog's Forge", "Mokosh's Spindle", "Beltane Fires", "The Cailleach's Winter",
            "Perun's Oak", "Domovoi's Hearth", "Rusalka Week", "Lugh's Harvest",
        };

        /// <summary>
        /// The seasons whose keepsakes exist. A season missing here still runs -
        /// it has a name, a frame and the whole track - but its avatar and pet
        /// levels pay a fallback chest until its paintings are added
        /// (tools/assets/gemini.json holds the prompts that made Veles').
        /// </summary>
        private static readonly ChronicleSeason[] Defined =
        {
            // Veles: the horned lord of cattle, wealth and the underworld - so
            // his pet is a golden aurochs and it brings gold.
            new(1, SeasonNames[0],
                "avatar_chronicle_veles", "Veles", "Chronicle/veles_avatar.webp",
                "frame_chronicle_veles", "Veles' Horns",
                "pet_chronicle_aurochs", "Golden Aurochs", PetStat.Gold, "Chronicle/veles_pet.webp"),
        };

        /// <summary>+12% to one stat (owner) - Black Serpent's single bonus, under the rare monster pets' 16%.</summary>
        public const int PetPct = 12;

        public static int SeasonNumberFor(int dayKey) => Math.Max(1, (dayKey - FirstSeasonDayKey) / SeasonDays + 1);

        public static int StartDayOf(int season) => FirstSeasonDayKey + (season - 1) * SeasonDays;
        public static int EndDayOf(int season) => StartDayOf(season) + SeasonDays - 1;

        public static ChronicleSeason Season(int number)
        {
            var defined = Defined.FirstOrDefault(s => s.Number == number);
            if (defined != null) return defined;
            string name = SeasonNames[(number - 1) % SeasonNames.Length];
            string slug = $"s{number}";
            return new ChronicleSeason(number, name,
                $"avatar_chronicle_{slug}", name, null,
                $"frame_chronicle_{slug}", name + " Frame",
                $"pet_chronicle_{slug}", name, PetStat.Gold, null);
        }

        /// <summary>Every season that has keepsakes - registered as cosmetics and pets.</summary>
        public static IReadOnlyList<ChronicleSeason> SeasonsWithArt => Defined.Where(s => s.HasArt).ToArray();

        public static int LevelFor(int xp) => Math.Clamp(xp / XpPerLevel, 0, Levels);

        public static int OverflowChestsFor(int xp) => Math.Max(0, xp - MaxXpForLevels) / OverflowXpPerChest;

        private static readonly Dictionary<int, ChronicleReward> ChestAt = new()
        {
            [5] = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Gold),
            [10] = new(ChronicleRewardKind.WardrobeChest, CosmeticRegistry.Legendary),
            [15] = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Gold),
            [20] = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Ancient),
            [25] = new(ChronicleRewardKind.Avatar, 1),
            [30] = new(ChronicleRewardKind.WardrobeChest, CosmeticRegistry.Legendary),
            [35] = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Gold),
            [40] = new(ChronicleRewardKind.Frame, 1),
            [45] = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Ancient),
            [50] = new(ChronicleRewardKind.Pet, 1),
        };

        private static readonly HashSet<int> BonusDiamondLevels = new() { 10, 20, 25, 30, 40, 45 };
        public const int BonusDiamonds = 25;

        /// <summary>What level <paramref name="level"/> pays in <paramref name="season"/>.</summary>
        public static IReadOnlyList<ChronicleReward> RewardsFor(int level, ChronicleSeason season)
        {
            var list = new List<ChronicleReward>(2);
            switch (level % 5)
            {
                case 1: list.Add(new(ChronicleRewardKind.Gold, 60)); break;
                case 2: list.Add(new(ChronicleRewardKind.Tickets, 1)); break;
                case 3: list.Add(new(ChronicleRewardKind.Diamonds, 15)); break;
                case 4: list.Add(new(ChronicleRewardKind.Gold, 90)); break;
                default:
                    var chest = ChestAt[level];
                    // A season with no paintings yet pays a chest where its
                    // avatar and pet would be, so its levels are never empty.
                    if (!season.HasArt && chest.Kind == ChronicleRewardKind.Avatar)
                        chest = new(ChronicleRewardKind.WardrobeChest, CosmeticRegistry.Legendary);
                    else if (!season.HasArt && chest.Kind == ChronicleRewardKind.Pet)
                        chest = new(ChronicleRewardKind.LootChest, (int)LootChestTier.Ancient);
                    list.Add(chest);
                    break;
            }
            if (BonusDiamondLevels.Contains(level)) list.Add(new(ChronicleRewardKind.Diamonds, BonusDiamonds));
            return list;
        }

        /// <summary>The cosmetics the seasons with art add to CosmeticRegistry: bound, never in a chest.</summary>
        public static IEnumerable<CosmeticDefinition> Cosmetics()
        {
            foreach (var s in SeasonsWithArt)
            {
                yield return new CosmeticDefinition(s.AvatarId, CosmeticKind.Avatar, CosmeticRegistry.Legendary, s.AvatarName, s.AvatarArt, Bound: true);
                yield return new CosmeticDefinition(s.FrameId, CosmeticKind.Frame, CosmeticRegistry.Legendary, s.FrameName, null, Bound: true);
            }
        }

        /// <summary>The pets the seasons with art add to PetRegistry.</summary>
        public static IEnumerable<PetDefinition> Pets()
            => SeasonsWithArt.Select(s => new PetDefinition(s.PetId, s.PetName, PetSource.Chronicle, 0, s.PetArt!,
                new[] { new PetBonus(s.PetStat, PetPct) }));
    }
}
