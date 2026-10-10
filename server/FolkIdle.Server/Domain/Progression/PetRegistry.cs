using System;
using System.Collections.Generic;
using System.Linq;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>What a pet improves. Each maps onto one EquippedAffixTotals field.</summary>
    public enum PetStat : byte
    {
        Xp = 1,
        Gold = 2,
        Damage = 3,
        WorldBossDamage = 4,
        CritDamage = 5,
        GatherSpeed = 6,
        DropChance = 7,
    }

    /// <summary>How a pet is acquired.</summary>
    public enum PetSource : byte
    {
        /// <summary>Bought in the event shop.</summary>
        Shop = 1,
        /// <summary>A rare drop alongside the event currency.</summary>
        RareDrop = 2,
        /// <summary>The seasonal boss's top tier, first clear.</summary>
        Boss = 3,
    }

    public sealed record PetBonus(PetStat Stat, int Pct);

    /// <param name="Id">Stable string key, stored on player_pets - never a position.</param>
    /// <param name="Art">Sprite path under /sprites/.</param>
    public sealed record PetDefinition(string Id, string Name, PetSource Source, int EventId, string Art, IReadOnlyList<PetBonus> Bonuses);

    /// <summary>
    /// The seasonal event pets. Plan: docs/superpowers/plans/2026-10-09-samhain-event.md.
    ///
    /// Modul: STRENGTH IS THE OWNER'S (2026-10-09): a shop pet +5% to one stat,
    /// the rare drop +8% to two, the boss pet the strongest. One pet per
    /// CHARACTER and each owned once per account, so the most a character
    /// carries is ONE pet's bonuses - PowerCeilingTests prints that lever, and
    /// PetTests pins that no pet can be on two characters.
    ///
    /// Modul: PETS ARE PERMANENT. They outlive their event and a rebirth (the
    /// rollover never touches player_pets); the currency is what expires.
    /// </summary>
    public static class PetRegistry
    {
        public const int ShopPetPct = 5;
        public const int RarePetPct = 8;
        public const int BossPetPct = 10;

        /// <summary>
        /// Chance that one event-currency drop also brings the rare pet.
        /// Per currency unit rather than per action, because the currency
        /// rates already pay an hour of combat and an hour of gathering about
        /// the same - so this inherits that fairness. At ~750 pumpkins a day
        /// (a busy account, after the 2026-10-10 halving) the Witch comes in
        /// about a week; at ~125 (a lone new character) it is a long shot,
        /// which is what rare means. Doubled with the halving, so the Witch
        /// stayed as rare in days as she was.
        /// </summary>
        public const double RareDropPerCurrency = 1.0 / 5_000.0;

        public static readonly IReadOnlyList<PetDefinition> All = new[]
        {
            Shop("ghostie", "Ghostie", PetStat.Xp),
            Shop("pixie", "Pixie", PetStat.Gold),
            Shop("skeleton_dog", "Skeleton Dog", PetStat.GatherSpeed),
            Shop("black_cat", "Black Cat", PetStat.DropChance),
            Shop("wolf_pup", "Wolf Pup", PetStat.CritDamage),
            new PetDefinition("pet_witch", "Witch", PetSource.RareDrop, SeasonalEventRegistry.SamhainId,
                "Events/samhain/pets/witch.webp",
                new[] { new PetBonus(PetStat.WorldBossDamage, RarePetPct), new PetBonus(PetStat.DropChance, RarePetPct) }),
            // Owner, 2026-10-10: the Mini Vampire is the boss's pet, the Wolf
            // Pup a shop one.
            new PetDefinition("pet_mini_vampire", "Mini Vampire", PetSource.Boss, SeasonalEventRegistry.SamhainId,
                "Events/samhain/pets/mini_vampire.webp",
                new[] { new PetBonus(PetStat.Damage, BossPetPct), new PetBonus(PetStat.WorldBossDamage, BossPetPct) }),
        };

        private static PetDefinition Shop(string slug, string name, PetStat stat)
            => new("pet_" + slug, name, PetSource.Shop, SeasonalEventRegistry.SamhainId,
                   "Events/samhain/pets/" + slug + ".webp", new[] { new PetBonus(stat, ShopPetPct) });

        private static readonly Dictionary<string, PetDefinition> ById = All.ToDictionary(p => p.Id, StringComparer.Ordinal);

        public static PetDefinition? Find(string? id) => id != null && ById.TryGetValue(id, out var p) ? p : null;

        /// <summary>The rare-drop pet of an event, if it has one.</summary>
        public static PetDefinition? RareDropOf(int eventId)
            => All.FirstOrDefault(p => p.Source == PetSource.RareDrop && p.EventId == eventId);

        /// <summary>The boss pet of an event, if it has one.</summary>
        public static PetDefinition? BossPetOf(int eventId)
            => All.FirstOrDefault(p => p.Source == PetSource.Boss && p.EventId == eventId);

        /// <summary>Folds a pet's bonuses into a character's totals (tenths of a percent).</summary>
        public static void AddTo(PetDefinition pet, ref EquippedAffixTotals totals)
        {
            foreach (var bonus in pet.Bonuses)
            {
                int tenths = bonus.Pct * 10;
                switch (bonus.Stat)
                {
                    case PetStat.Xp: totals.XpTenthsPct += tenths; break;
                    case PetStat.Gold: totals.GoldTenthsPct += tenths; break;
                    case PetStat.Damage: totals.DamageTenthsPct += tenths; break;
                    case PetStat.WorldBossDamage: totals.WorldBossDamageTenthsPct += tenths; break;
                    case PetStat.CritDamage: totals.CritDamageTenthsPct += tenths; break;
                    case PetStat.GatherSpeed: totals.GatherSpeedTenthsPct += tenths; break;
                    case PetStat.DropChance: totals.LootLuckTenthsPct += tenths; break;
                }
            }
        }

        public static string Describe(PetBonus bonus) => bonus.Stat switch
        {
            PetStat.Xp => $"+{bonus.Pct}% combat XP",
            PetStat.Gold => $"+{bonus.Pct}% gold from kills",
            PetStat.Damage => $"+{bonus.Pct}% damage",
            PetStat.WorldBossDamage => $"+{bonus.Pct}% world boss damage",
            PetStat.CritDamage => $"+{bonus.Pct}% critical damage",
            PetStat.GatherSpeed => $"+{bonus.Pct}% gathering speed",
            PetStat.DropChance => $"+{bonus.Pct}% drop chance",
            _ => string.Empty,
        };
    }
}
