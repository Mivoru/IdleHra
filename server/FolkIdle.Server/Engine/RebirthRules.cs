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
    }
}
