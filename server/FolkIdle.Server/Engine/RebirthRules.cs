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

        /// <summary>
        /// The lowest level a rebirth can be taken at at all.
        /// </summary>
        /// <remarks>
        /// Modul: A REBIRTH AT LEVEL 1 WAS FREE (owner, 2026-11-09). Rebirth had
        /// no level floor - only Renown asked for level 50 - and every other
        /// reward (skill points, bloodline ceiling, avatars, the Phoenix, the
        /// second pet slot, shards) paid at any level. A level-1 life has no
        /// gear or gold to lose, so two players typed REBIRTH twenty times in a
        /// few minutes and took the whole road. The floor is the Renown level:
        /// one number for "a life worth ending".
        /// </remarks>
        public const int MinimumLevel = RenownLevel;

        public static bool IsLevelEnough(int level) => level >= MinimumLevel;

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
            // Counted only up to RewardedRebirths: rebirths 11-20 raise the
            // bloodline ceiling and nothing else.
            renownedRebirths = Math.Min(renownedRebirths, RewardedRebirths);
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
        /// The rebirths that raise the bloodline ceiling: it reaches
        /// BreedingAptitudes.MaxValue at this count (BreedingAptitudes.CeilingFor).
        /// </summary>
        /// <remarks>
        /// Modul: TWENTY, BUT THE POWER STOPS AT TEN (owner 2026-10-14). The
        /// bloodline is the long game and takes twenty rebirths to max; the
        /// damage bonus, the skill points, the avatars and the Phoenix all
        /// stop at ten, because every one of them multiplies by the count and
        /// ten is the ceiling that keeps a newcomer within reach of a veteran.
        ///
        /// Modul: NO LONGER A CAP ON REBIRTH (2026-11-13). This was MaxRebirths
        /// and the rebirth refused past it, so both 20-rebirth accounts had
        /// nothing left to rebirth for. Rebirth is unlimited now: past twenty it
        /// pays Legacy Shards (ShardsForLevel, spent on the Ancestral Boons) and
        /// the title milestones below - never power that multiplies by the count.
        /// </remarks>
        public const int BloodlineRebirths = 20;

        /// <summary>
        /// The last rebirth that pays anything besides the bloodline ceiling:
        /// skill points, Renown's damage bonus, the milestone avatars, the title
        /// and the Phoenix.
        /// </summary>
        public const int RewardedRebirths = 10;

        /// <summary>
        /// Skill points each rebirth adds to the tree, permanently, beside the
        /// ones levels and Seals pay. The owner chose these over a separate
        /// rebirth tree - the game has enough trees already.
        /// </summary>
        public const int SkillPointsPerRebirth = 2;

        /// <summary>
        /// Combat XP bonus on every level below the level the previous life
        /// ended at - a flat 50%, whatever the count (owner: faster, "not crazy").
        /// </summary>
        public const int ReLevelXpBonusPct = 50;

        /// <summary>Rebirths that open each character's second pet slot.</summary>
        public const int SecondPetSlotRebirths = 5;

        /// <summary>The cheapest piece's price to carry through a rebirth (quality tier 1, Normal).</summary>
        public const long KeepPriceFloor = 5_000_000L;

        /// <summary>The dearest piece's price (quality tier 14, Transcendent).</summary>
        public const long KeepPriceCeiling = 100_000_000L;

        /// <summary>The highest quality tier an item can have (RarityTier.Transcendent).</summary>
        public const int TopQualityTier = 14;

        // ------------------------------------------------------------------
        // Modul: SHARDS BY HOW FAR THE LIFE WENT (2026-11-13). The payout was
        // 12.5 log10(gold) + 0.05 level^2 + 1.5 x inventory score, and the
        // inventory term dominated (about 2,000 of a typical 2,800): hoarding
        // gear right before a rebirth paid, the level barely did, and nobody
        // could have predicted the number. Now it is the level the life
        // reached, squared and halved - shown in the preview before the player
        // types REBIRTH. A level-50 life pays 1,250, a level-90 life 4,050,
        // level 100 pays 5,000. The level-50 floor (MinimumLevel) still stops
        // a rebirth taken for its own sake.
        // ------------------------------------------------------------------

        /// <summary>Legacy Shards a rebirth taken at this level pays.</summary>
        public static int ShardsForLevel(int level)
        {
            long l = Math.Max(0, level);
            return (int)Math.Min(int.MaxValue, l * l / 2);
        }

        public sealed record TitleMilestone(int Rebirths, string TitleSlug, string Name);

        /// <summary>
        /// The titles past the rewarded ten: cosmetic only, so the count can keep
        /// climbing without the power gap growing. Granted when a rebirth brings
        /// the count to or past the milestone (idempotent), so an account that
        /// was already there before this list existed gets it on its next rebirth.
        /// </summary>
        public static readonly System.Collections.Generic.IReadOnlyList<TitleMilestone> TitleMilestones = new[]
        {
            new TitleMilestone(20, "rebirth_bloodline", "Keeper of the Blood"),
            new TitleMilestone(30, "rebirth_undying", "The Undying"),
            new TitleMilestone(50, "rebirth_wheel", "Wheel of Ages"),
        };

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
        public static int SkillPointsFor(int rebirthCount) => Math.Clamp(rebirthCount, 0, RewardedRebirths) * SkillPointsPerRebirth;

        /// <summary>Whether this rebirth is the one that brings the title and the Phoenix.</summary>
        public static bool IsFinalRewardedRebirth(int rebirthCountAfter) => rebirthCountAfter == RewardedRebirths;

        /// <summary>
        /// The re-level XP bonus, in percent: paid only below the level the
        /// previous life ended at. An account that never rebirthed has no best
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
            // Quality tiers run 1 (Normal) to 14 (Transcendent); 0 is no tier.
            int tier = Math.Clamp(qualityTier, 1, TopQualityTier);
            double ratio = (double)KeepPriceCeiling / KeepPriceFloor;
            double price = KeepPriceFloor * Math.Pow(ratio, (double)(tier - 1) / (TopQualityTier - 1));
            return (long)Math.Round(price / 1_000_000.0) * 1_000_000L;
        }
    }
}
