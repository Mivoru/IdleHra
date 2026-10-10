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
        /// <summary>The whole health bar, layered like inheritance (monster pets, 2026-10-10).</summary>
        MaxHp = 8,
        /// <summary>Armour, after gear and milestones.</summary>
        Armour = 9,
        /// <summary>Extra harvest rolls, in the unit of every other yield bonus.</summary>
        GatherYield = 10,
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
        /// <summary>Any kill of any monster, any time - no event (owner, 2026-10-10).</summary>
        Monster = 4,
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
        /// A monster pet: +8% to ONE stat (owner, 2026-10-10) - better than a
        /// shop pet's +5%, short of the Witch's +8% to two.
        /// </summary>
        public const int MonsterPetPct = 8;

        /// <summary>
        /// The chance one kill brings a monster pet (owner, 2026-10-10: "on
        /// average one every 7-10 days"). Measured on production over the week
        /// before: 14,200 kills a day on the owner's account (one per ~7 days),
        /// 10,900 on an ordinary active one (~9), 32,600 on the fastest farmer
        /// (~3). Per kill rather than per hour because the owner asked for it
        /// on every kill; the fast farmer being luckier is the accepted cost.
        /// </summary>
        public const double MonsterPetPerKill = 1.0 / 100_000.0;

        /// <summary>
        /// Diamonds mailed for a monster pet the account already has (owner,
        /// 2026-10-10). Not 100: once the fastest farmer owns the pool, a
        /// duplicate every ~3 days at 100 would be ~230 a week - four times
        /// the Delve's calibrated 60-a-week ceiling. At 50 it is ~115.
        /// </summary>
        public const int DuplicateDiamonds = 50;

        /// <summary>
        /// Chance that one event-currency drop also brings the rare pet.
        /// Per currency unit rather than per action, because the currency
        /// rates already pay an hour of combat and an hour of gathering about
        /// the same - so this inherits that fairness. Re-anchored with the
        /// 25x pumpkin cut (2026-10-10 morning): a busy account earns ~240 a
        /// day, so at 1/2,000 the Witch comes in about a week or two and most
        /// busy accounts meet her before the event ends; a lone new character
        /// is a long shot, which is what rare means.
        ///
        /// 1/10,000 since the evening of 2026-10-10: pumpkins went up 5x, and
        /// the Witch is meant to stay exactly as rare PER KILL (about one in
        /// two million), so her per-pumpkin odds went down by the same 5x.
        /// </summary>
        public const double RareDropPerCurrency = 1.0 / 10_000.0;

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

            // Owner, 2026-10-10: ten monster pets, the stat each one carries
            // agreed with the owner. Ids are stable strings - never positions.
            Monster("hugin", "Hugin", PetStat.Xp),
            Monster("nisse", "Nisse", PetStat.Gold),
            Monster("cu_sidhe", "Cú Sídhe", PetStat.Damage),
            Monster("cait_sidhe", "Cait Sídhe", PetStat.CritDamage),
            Monster("ignis_fatuus", "Ignis Fatuus", PetStat.DropChance),
            Monster("kikimora", "Kikimora", PetStat.GatherSpeed),
            Monster("backahast", "Bäckahäst", PetStat.WorldBossDamage),
            Monster("llamhigyn_y_dwr", "Llamhigyn y Dŵr", PetStat.MaxHp),
            Monster("ogham_monolith", "Ogham Monolith", PetStat.Armour),
            Monster("dagdas_cauldron", "Dagda's Cauldron", PetStat.GatherYield),
        };

        private static PetDefinition Monster(string slug, string name, PetStat stat)
            => new("pet_" + slug, name, PetSource.Monster, 0,
                   "Pets/" + slug + ".webp", new[] { new PetBonus(stat, MonsterPetPct) });

        /// <summary>The monster pets, in registry order - the pool a kill draws from.</summary>
        public static readonly IReadOnlyList<PetDefinition> MonsterPool = All.Where(p => p.Source == PetSource.Monster).ToArray();

        /// <summary>
        /// Rolls <paramref name="kills"/> kills for a monster pet and queues
        /// every find for PetEngine. Called by the live kill and the offline
        /// window alike, so being away pays the same odds as watching. A
        /// window of tens of thousands of kills draws by expectation
        /// (SeasonalEventEarning.Draw), so the cost does not grow with it.
        /// </summary>
        public static int RollMonsterPets(long playerId, long kills, Random random)
        {
            if (playerId <= 0 || kills <= 0 || MonsterPool.Count == 0) return 0;
            int found = SeasonalEventEarning.Draw((int)Math.Min(kills, int.MaxValue), MonsterPetPerKill, random);
            for (int i = 0; i < found; i++)
            {
                var pet = MonsterPool[random.Next(MonsterPool.Count)];
                PetEngine.Drops.Enqueue(new PetDropNote(playerId, pet.Id));
            }
            return found;
        }

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
                    case PetStat.MaxHp: totals.HpTenthsPct += tenths; break;
                    case PetStat.Armour: totals.ArmourTenthsPct += tenths; break;
                    case PetStat.GatherYield: totals.GatherYieldTenthsPct += tenths; break;
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
            PetStat.DropChance => $"+{bonus.Pct}% loot luck (rarer drops)",
            PetStat.MaxHp => $"+{bonus.Pct}% health",
            PetStat.Armour => $"+{bonus.Pct}% armour",
            PetStat.GatherYield => $"+{bonus.Pct}% gathering yield",
            _ => string.Empty,
        };
    }
}
