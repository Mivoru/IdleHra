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
    /// the characters that predate the column and every future birth name
    /// itself, without either storing a seed or consulting the other. The same
    /// Guid always draws the same name.
    ///
    /// Modul: THE NAMES ARE OLD CELTIC - Old Irish, Middle Welsh, Gaulish and
    /// Brittonic - by the owner's call on 2026-09-13. They were Czech for one
    /// day; those two tables are kept below as LegacyCzech* ONLY so the backfill
    /// can recognise a name this registry handed out and replace it, without
    /// ever touching a name that came from anywhere else.
    ///
    /// PURE AND STATIC like BreedingAptitudes beside it - no database, no session.
    /// </summary>
    public static class FolkNameRegistry
    {
        private static readonly string[] MaleNames =
        {
            "Áed", "Ailill", "Amergin", "Bedwyr", "Bran", "Brennus", "Cadell", "Cadoc",
            "Cathal", "Cian", "Cináed", "Colum", "Conall", "Cormac", "Cynan", "Diarmait",
            "Donnchad", "Dubthach", "Emrys", "Eógan", "Fergus", "Fiachra", "Finnian", "Geraint",
            "Gwalchmai", "Gwydion", "Idris", "Lugaid", "Manawydan", "Niall", "Oisín", "Owain",
            "Peredur", "Pwyll", "Rhodri", "Ruadán", "Senán", "Taliesin", "Tadg", "Ambiorix",
        };

        private static readonly string[] FemaleNames =
        {
            "Aífe", "Áine", "Arianrhod", "Blodeuwedd", "Boudica", "Branwen", "Brigid", "Cartimandua",
            "Ceridwen", "Créd", "Dechtire", "Deirdre", "Derbforgaill", "Eithne", "Elen", "Emer",
            "Enid", "Étaín", "Fand", "Findabair", "Gráinne", "Gwenllian", "Líadan", "Luned",
            "Macha", "Medb", "Morfudd", "Muirenn", "Nessa", "Nia", "Niamh", "Olwen",
            "Rhiannon", "Sadb", "Scáthach", "Tailtiu", "Tangwystl", "Úna", "Mór", "Gwenhwyfar",
        };

        // The tables this registry used on 2026-09-12. Read by IsLegacyName and
        // nothing else - see the class comment.
        private static readonly string[] LegacyCzechMaleNames =
        {
            "Vojtěch", "Radomír", "Bohumil", "Jaromír", "Zdeslav", "Květoslav",
            "Miroslav", "Vlastimil", "Přemysl", "Břetislav", "Ctibor", "Dobroslav",
            "Hostivít", "Ludomír", "Mstislav", "Nezamysl", "Oldřich", "Radovan",
            "Slavomír", "Vratislav", "Záviš", "Bořivoj", "Chval", "Drahoslav",
            "Jarohněv", "Lubor", "Miloslav", "Nepomuk", "Ratibor", "Svatopluk",
            "Vítězslav", "Želibor",
        };

        private static readonly string[] LegacyCzechFemaleNames =
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
            Span<byte> bytes = stackalloc byte[16];
            characterId.TryWriteBytes(bytes);
            return Pick(bytes, isFemale);
        }

        /// <summary>
        /// The name for somebody in the village gene pool.
        ///
        /// Modul: NEWCOMERS WERE "Human woman". A person with no name on the one
        /// screen that asks you to marry them. Derived from the row id rather
        /// than stored, so there is no column, no migration and no backfill:
        /// the id is permanent for the life of the row, and the row does not
        /// survive the season anyway.
        /// </summary>
        public static string ForNewcomer(long newcomerId, bool isFemale)
        {
            Span<byte> bytes = stackalloc byte[8];
            BitConverter.TryWriteBytes(bytes, newcomerId);
            return Pick(bytes, isFemale);
        }

        /// <summary>
        /// Whether a name is one this registry handed out before the Celtic
        /// tables replaced the Czech ones. The backfill renames exactly these.
        /// </summary>
        public static bool IsLegacyName(string? name)
            => !string.IsNullOrEmpty(name)
               && (Array.IndexOf(LegacyCzechMaleNames, name) >= 0 || Array.IndexOf(LegacyCzechFemaleNames, name) >= 0);

        /// <summary>
        /// Both legacy tables as one list, so the backfill can hand it to EF as
        /// an IN clause instead of loading every character to test in C#.
        /// </summary>
        public static string[] LegacyNames()
        {
            var all = new string[LegacyCzechMaleNames.Length + LegacyCzechFemaleNames.Length];
            LegacyCzechMaleNames.CopyTo(all, 0);
            LegacyCzechFemaleNames.CopyTo(all, LegacyCzechMaleNames.Length);
            return all;
        }

        private static string Pick(ReadOnlySpan<byte> bytes, bool isFemale)
        {
            var table = isFemale ? FemaleNames : MaleNames;
            return table[StableIndex(bytes, table.Length)];
        }

        /// <summary>
        /// A stable, platform-independent index from an id's bytes.
        ///
        /// NOT Guid.GetHashCode(). That is randomised per process on some
        /// runtimes and is explicitly not guaranteed stable across versions, so
        /// a backfill run on the box and a birth run later could disagree about
        /// the same id - the exact drift this method exists to prevent. The
        /// bytes are hashed by hand instead, which is fixed for ever.
        /// </summary>
        private static int StableIndex(ReadOnlySpan<byte> bytes, int modulus)
        {
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

        /// <summary>Every name either table can produce, for tests.</summary>
        public static System.Collections.Generic.IReadOnlyList<string> AllMaleNames => MaleNames;
        public static System.Collections.Generic.IReadOnlyList<string> AllFemaleNames => FemaleNames;
    }
}
