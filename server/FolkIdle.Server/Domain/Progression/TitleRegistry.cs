using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>
    /// One title: a stable slug, the name a player sees, the Deep floor that
    /// earns it (0 = not a Deep title), and the colour it is drawn in.
    /// </summary>
    public sealed record TitleDefinition(string Slug, string DisplayName, int DeepFloor, string Color);

    /// <summary>
    /// Every title in the game, static in code.
    ///
    /// Modul: A MINIMAL TITLE SYSTEM, FIRST EARNED IN THE DEEP (task 37 spec §4).
    /// Before it, nothing in the game was purely a mark of having done
    /// something - every reward was power or currency, which is exactly what a
    /// bottomless gold sink must not pay. A title costs the economy nothing.
    ///
    /// SLUGS ARE STRINGS, NEVER POSITIONS. An ordered list that crosses the wire
    /// as an index drifted ten of twelve entries once (auto-reroll's
    /// KNOWN_AFFIX_IDS), so a player's title is stored and sent by slug, and
    /// the client renders the display name the SERVER sends and keeps no list
    /// of its own. Renaming a title is a one-line change here that touches no
    /// earned row. Names final by the owner, 2026-09-24; Patronage (spec §7)
    /// will add its patron_* titles to this same list.
    /// </summary>
    public static class TitleRegistry
    {
        // Modul: declared ABOVE All. Static initialisers run in text order,
        // and Build() reads this - below All it was still null and the whole
        // registry failed to initialise.
        /// <summary>One hue per region boss, so "Wolfbane IV" and "Wyrmbane IV" read apart at a glance.</summary>
        private static readonly string[] AscensionColorByRegion = { "#7ed957", "#ff9f43", "#5ad1ff", "#ff5e5e", "#ffd23f" };

        public static readonly IReadOnlyList<TitleDefinition> All = Build();

        private static IReadOnlyList<TitleDefinition> Build()
        {
            // Modul: THE COLOUR IS THE SERVER'S, like the name (2026-10-09). A
            // title is drawn as a chip in its own colour next to a player's name
            // in chat, on the boards and in rosters; the client renders the hex
            // it is sent and keeps no table of its own. The Deep cools from lamp
            // light to the abyss as it goes down.
            var list = new List<TitleDefinition>
            {
                new TitleDefinition("deep_10", "Lamplighter", 10, "#f2c14e"),
                new TitleDefinition("deep_15", "Deepwalker", 15, "#4fd1c5"),
                new TitleDefinition("deep_20", "Of the Dark Water", 20, "#3ba7f0"),
                new TitleDefinition("deep_30", "Lantern-Eater", 30, "#6c7cff"),
                new TitleDefinition("deep_40", "Where No Bell Rings", 40, "#a66cff"),
                new TitleDefinition("deep_50", "The Bottomless", 50, "#e05cff"),

                // Granted by hand only (the admin mail), never earned in play.
                new TitleDefinition(BetatesterSlug, "Betatester", 0, "#ff7ad9"),
                new TitleDefinition(DevSlug, "DEV", 0, "#ff4d4d"),
            };

            // Task 87: one title per Boss Ascension step per boss - a rank the
            // player wears ("Wolfbane IV"). DeepFloor 0 marks them as not the
            // Deep's, so ForDeepFloor and NextDeepTitle never see them. The names
            // come from BossAscensionRegistry, the one place the ladder lives.
            for (int region = Combat.BossAscensionRegistry.FirstRegion; region <= Combat.BossAscensionRegistry.LastRegion; region++)
            {
                for (int step = 1; step <= Combat.BossAscensionRegistry.MaxStep; step++)
                {
                    list.Add(new TitleDefinition(
                        Combat.BossAscensionRegistry.TitleSlug(region, step),
                        Combat.BossAscensionRegistry.TitleName(region, step), 0,
                        AscensionColor(region)));
                }
            }
            return list;
        }

        public const string BetatesterSlug = "betatester";
        public const string DevSlug = "dev";

        private static string AscensionColor(int region)
            => AscensionColorByRegion[Math.Clamp(region - Combat.BossAscensionRegistry.FirstRegion, 0, AscensionColorByRegion.Length - 1)];

        public static TitleDefinition? Find(string? slug)
            => slug == null ? null : All.FirstOrDefault(t => string.Equals(t.Slug, slug, StringComparison.Ordinal));

        public static string? DisplayNameFor(string? slug) => Find(slug)?.DisplayName;

        /// <summary>Every Deep title a player who has cleared <paramref name="floorCleared"/> has earned.</summary>
        public static IEnumerable<TitleDefinition> ForDeepFloor(int floorCleared)
            => All.Where(t => t.DeepFloor > 0 && t.DeepFloor <= floorCleared);

        /// <summary>The next Deep title past <paramref name="deepestFloor"/>, or null when every one is earned.</summary>
        public static TitleDefinition? NextDeepTitle(int deepestFloor)
            => All.Where(t => t.DeepFloor > deepestFloor).OrderBy(t => t.DeepFloor).FirstOrDefault();
    }
}
