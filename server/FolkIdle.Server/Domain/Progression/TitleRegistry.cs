using System;
using System.Collections.Generic;
using System.Linq;

namespace FolkIdle.Server.Domain.Progression
{
    /// <summary>One title: a stable slug, the name a player sees, and the Deep floor that earns it.</summary>
    public sealed record TitleDefinition(string Slug, string DisplayName, int DeepFloor);

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
        public static readonly IReadOnlyList<TitleDefinition> All = new[]
        {
            new TitleDefinition("deep_10", "Lamplighter", 10),
            new TitleDefinition("deep_15", "Deepwalker", 15),
            new TitleDefinition("deep_20", "Of the Dark Water", 20),
            new TitleDefinition("deep_30", "Lantern-Eater", 30),
            new TitleDefinition("deep_40", "Where No Bell Rings", 40),
            new TitleDefinition("deep_50", "The Bottomless", 50),
        };

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
