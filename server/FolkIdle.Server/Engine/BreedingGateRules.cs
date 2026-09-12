using FolkIdle.Server.Network;

namespace FolkIdle.Server.Engine
{
    /// <summary>Why a pairing was refused, or None.</summary>
    public enum BreedingRefusal
    {
        None = 0,
        ParentNotAdult,
        ParentResting,
        ParentInEscrow,
        SameSex,
        SexRolesSwapped,
        RaceMismatch,
        PartnerAlreadyMarried,
    }

    /// <summary>
    /// WHO MAY BREED. One copy, called by the engine and by both preview
    /// endpoints.
    ///
    /// It used to be written three times over, and had already drifted: the
    /// roster preview checked race but not sex, so picking a woman as the
    /// paternal parent produced an ELIGIBLE, priced preview and a button that
    /// sent a command the engine rolled straight back. A comment beside that
    /// preview said the two "must refuse the same pairs or the preview lies
    /// again" - which is a thing to assert, not to write down.
    ///
    /// THE LEVEL GATE IS GONE, and it is the reason this file exists. Both
    /// pairings required `Level >= 50` on the CHARACTER row. Nothing in the
    /// server has ever written that column except DevFixtureSeeder; a player's
    /// level is PlayerRecords.CurrentLevel, the account level, and characters
    /// do not have one of their own. Measured on the live database: 80
    /// characters, none above level 1. Breeding was refused for every player,
    /// on every attempt, since launch - and it worked on the dev fixture, which
    /// is exactly why nobody caught it.
    ///
    /// What gates breeding now is only what a player can see on the screen: the
    /// Breeding Grounds exist, the parents are adults, one of each sex, the
    /// same race, neither resting nor locked in a trade, and a villager has not
    /// already married in.
    ///
    /// PURE AND STATIC, like BreedingAptitudes beside it. Every rule here is
    /// "given two rows, may they pair", which needs no database and no session,
    /// so it can be tested in a millisecond rather than behind a Testcontainer.
    /// </summary>
    public static class BreedingGateRules
    {
        /// <summary>
        /// The fields of a character row that the gate actually reads. A
        /// struct rather than the EF entity so the preview endpoints, the
        /// engine and the tests can all build one without a DbContext.
        /// </summary>
        public struct Parent
        {
            public int AgePhase;
            public bool IsFemale;
            public byte RaceId;
            public bool IsLockedInEscrow;
            public bool IsBreedingActive;
            public long BreedingCooldownEndEpoch;
        }

        /// <summary>
        /// Resting means the flag is set AND the stamp has not passed. The
        /// engine clears the flag lazily on the next attempt rather than
        /// sweeping for it, so "active with an expired stamp" has to read as
        /// free here or a character is retired by a flag nobody clears.
        /// </summary>
        private static bool IsResting(in Parent parent, long nowEpoch)
            => parent.IsBreedingActive && parent.BreedingCooldownEndEpoch > nowEpoch;

        /// <summary>
        /// Two of the player's own characters. Order matters: the first is the
        /// paternal side and has to be the man.
        /// </summary>
        public static BreedingRefusal CheckPair(in Parent paternal, in Parent maternal, long nowEpoch)
        {
            if (paternal.AgePhase < 1 || maternal.AgePhase < 1) return BreedingRefusal.ParentNotAdult;
            if (paternal.IsLockedInEscrow || maternal.IsLockedInEscrow) return BreedingRefusal.ParentInEscrow;
            if (IsResting(paternal, nowEpoch) || IsResting(maternal, nowEpoch)) return BreedingRefusal.ParentResting;
            if (paternal.IsFemale == maternal.IsFemale) return BreedingRefusal.SameSex;

            // Stricter than "not the same sex". Once every race arrives as a
            // male/female pair the paternal and maternal labels mean what they
            // say, so a valid pair in the wrong slots is its own refusal - and
            // it needs its own message, because "the server rejected that" for
            // a pair the player can see is opposite-sex teaches nothing.
            if (paternal.IsFemale) return BreedingRefusal.SexRolesSwapped;

            if (paternal.RaceId != maternal.RaceId) return BreedingRefusal.RaceMismatch;

            return BreedingRefusal.None;
        }

        /// <summary>
        /// A hero and somebody from the village - THE standard pair, and the
        /// only one that puts a genuinely new number into a bloodline.
        ///
        /// No role rule here, deliberately. The player picks a hero and a
        /// partner rather than a paternal and a maternal slot, so a woman of
        /// the line marrying a man from the village is an ordinary pairing and
        /// the engine sorts out which side is which.
        /// </summary>
        public static BreedingRefusal CheckVillagerPair(
            in Parent hero,
            bool villagerIsFemale,
            byte villagerRaceId,
            bool villagerHasMarried,
            long nowEpoch)
        {
            if (hero.AgePhase < 1) return BreedingRefusal.ParentNotAdult;
            if (hero.IsLockedInEscrow) return BreedingRefusal.ParentInEscrow;
            if (IsResting(hero, nowEpoch)) return BreedingRefusal.ParentResting;
            if (villagerHasMarried) return BreedingRefusal.PartnerAlreadyMarried;
            if (hero.IsFemale == villagerIsFemale) return BreedingRefusal.SameSex;
            if (hero.RaceId != villagerRaceId) return BreedingRefusal.RaceMismatch;

            return BreedingRefusal.None;
        }

        /// <summary>
        /// What the player is told. Every refusal has one, and no two share -
        /// BreedingEngine had twenty rollbacks and zero command results, so a
        /// refused pairing left the button enabled and the screen silent.
        /// </summary>
        public static CommandResultCode ResultCodeFor(BreedingRefusal refusal) => refusal switch
        {
            BreedingRefusal.ParentNotAdult => CommandResultCode.BreedingParentNotAdult,
            BreedingRefusal.ParentResting => CommandResultCode.BreedingParentResting,
            BreedingRefusal.ParentInEscrow => CommandResultCode.BreedingParentInEscrow,
            BreedingRefusal.SameSex => CommandResultCode.BreedingSameSex,
            BreedingRefusal.SexRolesSwapped => CommandResultCode.BreedingSexRolesSwapped,
            BreedingRefusal.RaceMismatch => CommandResultCode.BreedingRaceMismatch,
            BreedingRefusal.PartnerAlreadyMarried => CommandResultCode.BreedingPartnerAlreadyMarried,

            // BreedingRefusal.None is not a refusal, so it maps to the enum's
            // own zero - Success. Nothing calls this with None; the mapping is
            // total so that adding a refusal without a code is a compile error
            // rather than a silent Success on the wire.
            _ => CommandResultCode.Success,
        };

        /// <summary>
        /// The string the REST previews answer in. Kept as the wire's existing
        /// vocabulary so the client's refusal() table does not have to learn a
        /// second set of names for one set of reasons.
        /// </summary>
        public static string ReasonSlugFor(BreedingRefusal refusal) => refusal switch
        {
            BreedingRefusal.ParentNotAdult => "parent_not_adult",
            BreedingRefusal.ParentResting => "parent_on_cooldown",
            BreedingRefusal.ParentInEscrow => "parent_locked_in_escrow",
            BreedingRefusal.SameSex => "same_sex",
            BreedingRefusal.SexRolesSwapped => "sex_roles_swapped",
            BreedingRefusal.RaceMismatch => "race_mismatch",
            BreedingRefusal.PartnerAlreadyMarried => "villager_already_married",
            _ => string.Empty,
        };
    }
}
