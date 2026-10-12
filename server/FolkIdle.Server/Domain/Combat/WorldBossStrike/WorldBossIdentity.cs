using System;
using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>
    /// Who the world boss IS: one monster in monsters.json, named from content.
    /// </summary>
    /// <remarks>
    /// Modul: THE BOSS HAD NO NAME (task 105). The encounter is an HP pool,
    /// five plates and a calendar, and nothing on the server said what was
    /// being struck. monsters.json id 30 - a row no region uses (the canon is
    /// ids 91-115) - is the boss, the screen reads its name off
    /// /api/v1/worldboss/board rather than keeping a copy, and renaming the
    /// boss is a content edit.
    ///
    /// Modul: CHERNOBOG (owner, 2026-10-13). Id 30 was "Perun's Celestial
    /// Avatar" and is Chernobog now, the black god Svarog sealed in a forged
    /// chest - who breaks its lock every Monday because the bargain left
    /// darkness half the world. Below a quarter of his health (the enraged
    /// shield wheel) he takes his other shape, the Black Serpent, and the screen
    /// swaps to that art. Spec: docs/superpowers/specs/2026-10-13-chernobog-world-boss-design.md.
    ///
    /// The same boss returns every Monday; a weekly roster would be a product
    /// decision and would live here.
    /// </remarks>
    public static class WorldBossIdentity
    {
        /// <summary>monsters.json "Chernobog".</summary>
        public const int MonsterId = 30;

        /// <summary>Only if the content failed to load - the board must still answer.</summary>
        public const string FallbackName = "Chernobog";

        /// <summary>Sprite paths under /sprites/ (tools/key_event_art.py cuts them from the owner's paintings).</summary>
        public const string Art = "WorldBoss/chernobog.webp";
        public const string EnragedArt = "WorldBoss/black_serpent.webp";

        public static string Name
        {
            get
            {
                try
                {
                    string name = ContentRegistry.GetMonsterName(MonsterId);
                    return string.IsNullOrWhiteSpace(name) ? FallbackName : name;
                }
                catch (IndexOutOfRangeException)
                {
                    return FallbackName;
                }
            }
        }
    }
}
