using System;

namespace FolkIdle.Server.Engine
{
    /// <summary>
    /// Rebirth on demand (task 88): the pure rules. The reset itself is
    /// SeasonalRotationEngine's rollover, run for one player by RebirthEngine.
    ///
    /// Modul: RENOWN IS A CURVE WITH A CEILING, deliberately. A rebirth can be
    /// repeated as often as a player likes, so anything it pays permanently is
    /// multiplied by a count only the player controls - linear-and-uncapped in
    /// the purest form. `floor(Max x (1 - Retain^n))` approaches Max and never
    /// reaches it: the first renowned rebirth is worth 3%, the tenth 13%, and
    /// nothing is worth 15%. PowerCeilingTests carries it as a lever.
    ///
    /// Modul: THE LEVEL GATE IS ON THE BONUS, NOT ON THE REBIRTH. The owner's
    /// words were "whenever they choose", so a rebirth is allowed at any level.
    /// But a bonus paid for rebirth itself would be maxed in minutes by
    /// rebirthing at level 2 on repeat; paying it only for a rebirth taken
    /// halfway to level 100 makes each step of it cost a real climb.
    /// </summary>
    public static class RebirthRules
    {
        /// <summary>The level a rebirth must be taken at to count toward Renown - halfway to the level-100 deed.</summary>
        public const int RenownLevel = 50;

        /// <summary>The asymptote of the damage bonus, in percent. Never reached.</summary>
        public const int MaxDamageBonusPct = 15;

        /// <summary>The share of the remaining headroom each renowned rebirth leaves.</summary>
        public const double Retain = 0.8;

        /// <summary>How far a rebirth raises the database epoch - see RebirthEngine.</summary>
        /// <remarks>
        /// Three, not one: a stale snapshot of the old life queued after the
        /// rebirth's own flush is stamped at most (flush epoch + k), and the
        /// row must be ahead of every such k for FlushState to refuse it. Kept
        /// inside ClientCommandValidator's EpochDriftTolerance (5), so a
        /// command the client sends before the reload lands is still accepted.
        /// </remarks>
        public const long EpochFence = 3L;

        /// <summary>
        /// Modul: THE WORD THE PLAYER TYPES (2026-10-08). Two players rebirthed
        /// by accident in one evening - the panel's second button sat exactly
        /// where its first had been, so a double tap was a rebirth. The request
        /// must now carry this word, typed by the player; a client that does
        /// not ask for it (an old bundle) is refused rather than trusted.
        /// Compared case-insensitively after trimming.
        /// </summary>
        public const string ConfirmationWord = "REBIRTH";

        public static bool IsConfirmed(string? typed)
            => typed != null && string.Equals(typed.Trim(), ConfirmationWord, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a rebirth taken at this level counts toward Renown.</summary>
        public static bool IsRenowned(int level) => level >= RenownLevel;

        /// <summary>The permanent damage bonus, in whole percent, for this many renowned rebirths.</summary>
        public static int DamageBonusPct(int renownedRebirths)
        {
            if (renownedRebirths <= 0) return 0;
            double pct = MaxDamageBonusPct * (1.0 - Math.Pow(Retain, renownedRebirths));
            // Floored, so the ceiling is a strict one: at any finite count the
            // raw value is below Max and the floor keeps it at Max - 1.
            return Math.Clamp((int)Math.Floor(pct + 1e-9), 0, MaxDamageBonusPct - 1);
        }

        // ------------------------------------------------------------------
        // Modul: A REBIRTH WORTH TAKING (task 114, owner 2026-10-11). Before
        // this, a rebirth wiped level, gold, gear, tree and attributes for
        // +3% damage, and no account had ever taken one. What it pays now is
        // listed here and nowhere else.
        // ------------------------------------------------------------------

        /// <summary>
        /// Rebirths an account may take. Every reward below multiplies by this
        /// count, so it is the ceiling that keeps a newcomer within reach.
        /// </summary>
        public const int MaxRebirths = 10;

        /// <summary>
        /// Skill points each rebirth adds to the tree, permanently, beside the
        /// ones levels and Seals pay. The owner chose these over a separate
        /// rebirth tree - the game has enough trees already.
        /// </summary>
        public const int SkillPointsPerRebirth = 2;

        /// <summary>
        /// Combat XP bonus on every level below the account's best-ever level
        /// - a flat 50%, whatever the count (owner: faster, "not crazy").
        /// </summary>
        public const int ReLevelXpBonusPct = 50;

        /// <summary>Rebirths that open each character's second pet slot.</summary>
        public const int SecondPetSlotRebirths = 5;

        /// <summary>The cheapest piece's price to carry through a rebirth (quality tier 0).</summary>
        public const long KeepPriceFloor = 5_000_000L;

        /// <summary>The dearest piece's price (quality tier 13, the top rarity).</summary>
        public const long KeepPriceCeiling = 100_000_000L;

        /// <summary>The highest quality tier an item can have.</summary>
        public const int TopQualityTier = 13;

        public static bool IsCapped(int rebirthCount) => rebirthCount >= MaxRebirths;

        public sealed record AvatarMilestone(int Rebirths, string AvatarId, string Name, string Art);

        /// <summary>
        /// The avatar each milestone rebirth grants (owner, 2026-10-11: the
        /// climb has steps - 3, 5, 7 and the last, 10). Ids are stable strings.
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlyList<AvatarMilestone> AvatarMilestones = new[]
        {
            new AvatarMilestone(3, "avatar_rebirth_jarilo", "Jarilo", "Rebirth/avatars/jarilo.webp"),
            new AvatarMilestone(5, "avatar_rebirth_vesna", "Vesna", "Rebirth/avatars/vesna.webp"),
            new AvatarMilestone(7, "avatar_rebirth_etain", "Étaín", "Rebirth/avatars/etain.webp"),
            new AvatarMilestone(10, "avatar_rebirth_morana", "Morana", "Rebirth/avatars/morana.webp"),
        };

        /// <summary>The skill points this many rebirths pay, capped with the count.</summary>
        public static int SkillPointsFor(int rebirthCount) => Math.Clamp(rebirthCount, 0, MaxRebirths) * SkillPointsPerRebirth;

        /// <summary>
        /// The re-level XP bonus, in percent: paid only below a best level an
        /// earlier life reached. An account that never rebirthed has no best
        /// level stored (0), so it never qualifies.
        /// </summary>
        public static int ReLevelXpBonus(int currentLevel, int peakLevel)
            => peakLevel > 0 && currentLevel < peakLevel ? ReLevelXpBonusPct : 0;

        /// <summary>
        /// Gold to keep one piece of equipment through a rebirth. Geometric in
        /// rarity from 5M to 100M (owner: priced by rarity so an ordinary
        /// player can keep a few good pieces, and a whole top set stays dear),
        /// rounded to the million.
        /// </summary>
        public static long KeepPrice(int qualityTier)
        {
            int tier = Math.Clamp(qualityTier, 0, TopQualityTier);
            double ratio = (double)KeepPriceCeiling / KeepPriceFloor;
            double price = KeepPriceFloor * Math.Pow(ratio, (double)tier / TopQualityTier);
            return (long)Math.Round(price / 1_000_000.0) * 1_000_000L;
        }
    }
}
