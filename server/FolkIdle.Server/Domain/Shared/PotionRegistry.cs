using System;
using System.Collections.Generic;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Shared
{
    /// <summary>What a potion does. One per brew, normal and strong alike.</summary>
    public enum PotionKind : byte
    {
        None = 0,
        Hunt,       // +XP from kills
        Fortune,    // +loot luck
        Goldmoss,   // +gold from kills
        Ironbark,   // +armour
        Swiftroot,  // +attack speed
        Bossbane,   // +damage against region and world bosses
        Gatherer,   // +gathering speed
        Kupala,     // +rare find while gathering
    }

    /// <summary>
    /// The three potion slots. Each holds one potion at a time and has its own
    /// shelf. Rewards rides the payload's old "offensive" fields and Combat the
    /// old "defensive" ones; Gathering is new (2026-10-31).
    /// </summary>
    public static class PotionSlot
    {
        public const int Rewards = 0;
        public const int Combat = 1;
        public const int Gathering = 2;
        public const int Count = 3;
    }

    public readonly record struct PotionDefinition(int ItemId, PotionKind Kind, int Slot, bool Strong, int DurationMs, int Magnitude);

    /// <summary>
    /// Every potion, read once from items.json by BaseId (herbs and potions
    /// phase, HERBS_POTIONS_ASCENSION.md 1.2a). Command-time and load-time
    /// lookups only; the tick reads the resolved <see cref="PotionEffects"/>.
    /// </summary>
    /// <remarks>
    /// Modul: MAGNITUDES IN ONE TABLE, measured against what already pays the
    /// same stat - see PotionMeasurementTests, which prints the comparison
    /// and runs the boss-wall scan with each combat potion active. Units are
    /// whole percent unless the row says tenths.
    ///
    /// Modul: IRONBARK IS SMALL ON PURPOSE (measured 2026-10-31). Armour is
    /// sustain, and sustain decides a boss wall: at +15% the set two quality
    /// tiers under the region 2 and 3 requirements won. The edges were +8%
    /// (normal) and +11% (strong); 6/9 sits a point under each. The damage
    /// potions (Swiftroot, Boss-bane) cut a kill 11-20% and hold every wall.
    /// </remarks>
    public static class PotionRegistry
    {
        public const int HourMs = 3_600_000;

        /// <summary>(kind, slot, duration, normal, strong).</summary>
        private static readonly (string Stem, PotionKind Kind, int Slot, int DurationMs, int Normal, int Strong)[] Table =
        {
            ("hunt_elixir",       PotionKind.Hunt,      PotionSlot.Rewards,   HourMs,     20, 30), // % XP
            ("fortune_draught",   PotionKind.Fortune,   PotionSlot.Rewards,   HourMs,     20, 30), // loot luck points
            ("goldmoss_tonic",    PotionKind.Goldmoss,  PotionSlot.Rewards,   HourMs,     20, 30), // % gold
            ("ironbark_brew",     PotionKind.Ironbark,  PotionSlot.Combat,    HourMs,      6,  9), // % armour - capped by the wall scan, see remarks
            ("swiftroot_tea",     PotionKind.Swiftroot, PotionSlot.Combat,    HourMs,     10, 15), // attack speed points
            ("bossbane_philtre",  PotionKind.Bossbane,  PotionSlot.Combat,    HourMs,     15, 25), // % damage to bosses
            ("gatherers_draught", PotionKind.Gatherer,  PotionSlot.Gathering, 2 * HourMs, 25, 40), // % gathering speed
            ("kupala_night",      PotionKind.Kupala,    PotionSlot.Gathering, 2 * HourMs, 50, 80), // rare find, TENTHS of a percent
        };

        private static Dictionary<int, PotionDefinition>? _byItemId;

        private static Dictionary<int, PotionDefinition> ById => _byItemId ??= Build();

        private static Dictionary<int, PotionDefinition> Build()
        {
            var map = new Dictionary<int, PotionDefinition>();
            foreach (var row in Table)
            {
                foreach (bool strong in new[] { false, true })
                {
                    string slotMarker = row.Slot switch
                    {
                        PotionSlot.Rewards => "offensive",
                        PotionSlot.Combat => "defensive",
                        _ => "gathering",
                    };
                    string baseId = (strong ? "strong_" : "") + row.Stem + "_" + slotMarker + "_potion_consumable";
                    if (ContentRegistry.TryGetItemDefinitionByBaseId(baseId, out var item))
                    {
                        map[item.Id] = new PotionDefinition(item.Id, row.Kind, row.Slot, strong, row.DurationMs, strong ? row.Strong : row.Normal);
                    }
                }
            }
            return map;
        }

        /// <summary>Test-only: forget the table so a reloaded ContentRegistry is read again.</summary>
        internal static void ResetForTests() => _byItemId = null;

        public static bool TryGet(int itemId, out PotionDefinition potion) => ById.TryGetValue(itemId, out potion);

        public static IEnumerable<PotionDefinition> All => ById.Values;

        /// <summary>The magnitude a slot's active potion pays for one kind, or 0.</summary>
        public static int MagnitudeOf(int activeItemId, PotionKind kind)
            => activeItemId > 0 && ById.TryGetValue(activeItemId, out var p) && p.Kind == kind ? p.Magnitude : 0;
    }

    /// <summary>
    /// What the three active potions add, resolved once per read from the
    /// payload's slot ids. A plain struct of ints, so the tick and the
    /// offline projection read it the same way.
    /// </summary>
    public readonly struct PotionEffects
    {
        public int XpPct { get; init; }
        public int LootLuckPct { get; init; }
        public int GoldPct { get; init; }
        public int ArmourPct { get; init; }
        public int AttackSpeedPct { get; init; }
        public int BossDamagePct { get; init; }
        public int GatherSpeedPct { get; init; }
        public int RareFindTenthsPct { get; init; }

        public static PotionEffects From(int rewardsItemId, int combatItemId, int gatheringItemId) => new()
        {
            XpPct = PotionRegistry.MagnitudeOf(rewardsItemId, PotionKind.Hunt),
            LootLuckPct = PotionRegistry.MagnitudeOf(rewardsItemId, PotionKind.Fortune),
            GoldPct = PotionRegistry.MagnitudeOf(rewardsItemId, PotionKind.Goldmoss),
            ArmourPct = PotionRegistry.MagnitudeOf(combatItemId, PotionKind.Ironbark),
            AttackSpeedPct = PotionRegistry.MagnitudeOf(combatItemId, PotionKind.Swiftroot),
            BossDamagePct = PotionRegistry.MagnitudeOf(combatItemId, PotionKind.Bossbane),
            GatherSpeedPct = PotionRegistry.MagnitudeOf(gatheringItemId, PotionKind.Gatherer),
            RareFindTenthsPct = PotionRegistry.MagnitudeOf(gatheringItemId, PotionKind.Kupala),
        };
    }
}
