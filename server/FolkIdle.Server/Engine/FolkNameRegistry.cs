using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// WHAT A CHARACTER IS CALLED.
    ///
    /// CharacterRecord had no name, so every screen that listed a character
    /// identified it by the first eight characters of a Guid. The breeding
    /// dropdown read like this:
    ///
    ///     Human man b6b704ca - lv 1, Elder, gen 0, needs 50
    ///     Vila woman 0214b4e9 - lv 1, Elder, gen 0, needs 50
    ///     Draugr man 4c34889f - lv 1, Elder, gen 0, needs 50
    ///
    /// Ten of those, and the player reporting it could not find his own main
    /// character in the list - correctly, because nothing in the list referred
    /// to it. A bloodline is the one system in this game that is supposed to be
    /// about people, and it was a table of hex.
    ///
    /// DETERMINISTIC IN THE ID, which is what lets CharacterNameBackfill name
    /// the 80 characters that predate the column and every future birth name
    /// itself, without either storing a seed or consulting the other. The same
    /// Guid always draws the same name.
    ///
    /// The names are Czech and Slavic, matching the game's setting and the
    /// races it is built from. PURE AND STATIC like BreedingAptitudes beside
    /// it - no database, no session.
    /// </summary>
    public static class FolkNameRegistry
    {
        private static readonly string[] MaleNames =
        {
            "Vojtěch", "Radomír", "Bohumil", "Jaromír", "Zdeslav", "Květoslav",
            "Miroslav", "Vlastimil", "Přemysl", "Břetislav", "Ctibor", "Dobroslav",
            "Hostivít", "Ludomír", "Mstislav", "Nezamysl", "Oldřich", "Radovan",
            "Slavomír", "Vratislav", "Záviš", "Bořivoj", "Chval", "Drahoslav",
            "Jarohněv", "Lubor", "Miloslav", "Nepomuk", "Ratibor", "Svatopluk",
            "Vítězslav", "Želibor",
        };

        private static readonly string[] FemaleNames =
        {
            "Zlata", "Milena", "Božena", "Vlasta", "Dobromila", "Jarmila",
            "Květoslava", "Ludmila", "Radomíra", "Svatava", "Vlastislava", "Zdislava",
            "Blažena", "Drahomíra", "Hostislava", "Krasomila", "Libuše", "Mstislava",
            "Nezamyslava", "Otylie", "Přibyslava", "Ratiborka", "Slavena", "Věnceslava",
            "Zbyslava", "Dobrava", "Jaroslava", "Lubomíra", "Miloslava", "Radoslava",
            "Světlana", "Živana",
        };

        /// <summary>
        /// The name for a given character. Stable for the life of the id, and
        /// drawn from the table matching the character's sex.
        /// </summary>
        public static string For(Guid characterId, bool isFemale)
        {
            var table = isFemale ? FemaleNames : MaleNames;
            return table[StableIndex(characterId, table.Length)];
        }

        /// <summary>
        /// A stable, platform-independent index from a Guid.
        ///
        /// NOT Guid.GetHashCode(). That is randomised per process on some
        /// runtimes and is explicitly not guaranteed stable across versions, so
        /// a backfill run on the box and a birth run later could disagree about
        /// the same id - the exact drift this method exists to prevent. The
        /// bytes are hashed by hand instead, which is fixed for ever.
        /// </summary>
        private static int StableIndex(Guid id, int modulus)
        {
            Span<byte> bytes = stackalloc byte[16];
            id.TryWriteBytes(bytes);

            // FNV-1a, 32-bit. Small, published, and fixed for ever - which is
            // the only property that matters here, because changing it would
            // rename every character in the database on the next deploy.
            // CharacterNameTests pins it for that reason.
            uint hash = 2166136261u;
            for (int i = 0; i < bytes.Length; i++)
            {
                hash ^= bytes[i];
                hash *= 16777619u;
            }

            return (int)(hash % (uint)modulus);
        }

        /// <summary>
        /// How many names each table holds. Public so a test can assert the
        /// spread is wide enough that a roster of three rarely collides.
        /// </summary>
        public static int MaleNameCount => MaleNames.Length;
        public static int FemaleNameCount => FemaleNames.Length;
    }
}
