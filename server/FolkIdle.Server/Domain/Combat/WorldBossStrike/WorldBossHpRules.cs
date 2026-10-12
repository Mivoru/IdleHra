using System;
using System.Collections.Generic;

namespace FolkIdle.Server.Domain.Combat.WorldBossStrike
{
    /// <summary>One registered player's registration, as the reveal reads it.</summary>
    public readonly record struct Registration(long Damage, int Strikes);

    /// <summary>
    /// How big Chernobog is, and who is suspected of keeping him small. Spec:
    /// docs/superpowers/specs/2026-10-13-chernobog-world-boss-design.md, sections 2 and 4.
    /// </summary>
    /// <remarks>
    /// Modul: THE HEALTH IS SET BY THE PEOPLE FIGHTING IT (owner, 2026-10-13).
    /// It was 50M x 1.5 x online accounts - 75M against a server that dealt
    /// about 100k a week, so the boss could not fall and "defeat" meant nothing.
    /// Now Monday and Tuesday are registration, and at the reveal each
    /// registered player's average daily damage (Monday, Tuesday, or both / 2 -
    /// one strike a day, so it is damage / strikes) is multiplied by a hidden N
    /// drawn from [5.5, 7.0]. N = 7 would need every registered player to strike
    /// every day as well as they did at the start; the range lets a week be a
    /// little kinder or a little harder, and nobody can compute the answer.
    /// The registration damage itself already counts against the total.
    ///
    /// The range is two constants so it can be retuned after the first weeks.
    /// </remarks>
    public static class WorldBossHpRules
    {
        public const double MinFactor = 5.5;
        public const double MaxFactor = 7.0;

        /// <summary>Registration lasts this long from the encounter's start: Monday and Tuesday.</summary>
        public const long RegistrationSeconds = 2 * 86_400L;

        /// <summary>A uniform draw in [MinFactor, MaxFactor] from a cryptographic source - a predictable N is a computable one.</summary>
        public static double DrawFactor()
        {
            double unit = System.Security.Cryptography.RandomNumberGenerator.GetInt32(1_000_001) / 1_000_000.0;
            return MinFactor + (MaxFactor - MinFactor) * unit;
        }

        /// <summary>sum(damage_i / strikes_i) x factor. 0 when nobody registered.</summary>
        public static long TotalHp(IEnumerable<Registration> registered, double factor)
        {
            double sum = 0;
            foreach (var r in registered)
            {
                if (r.Strikes <= 0 || r.Damage <= 0) continue;
                sum += (double)r.Damage / r.Strikes;
            }
            return (long)Math.Min(long.MaxValue / 4, Math.Round(sum * factor));
        }

        // --- sandbagging (spec section 4) ------------------------------------

        /// <summary>A registration this skilful or worse, on average, is "threw everything away".</summary>
        public const double SandbagRegistrationMax = 0.1;

        /// <summary>From the reveal on, at least this skilful on average...</summary>
        public const double SandbagLaterMin = 0.6;

        /// <summary>...over at least this many played strikes.</summary>
        public const int SandbagLaterMinStrikes = 3;

        /// <summary>A week counts as "showed the skill" at this mean over at least SandbagLaterMinStrikes played strikes.</summary>
        public const double ProvenSkillMin = 0.6;

        /// <remarks>
        /// Modul: SKILL, NOT DAMAGE. Damage rises through the week for an honest
        /// player - the crowd finds the weak plate by breaking the others, and
        /// gear improves - so comparing Monday's damage with Thursday's would flag
        /// the innocent. The skill score s (best four of five spears, Seam = 1)
        /// does not depend on the weak plate. A correct parry always lands its
        /// counter as a Seam (ShieldWheelScorer), so a player who knows the game
        /// scores on Monday too, and a sandbagger has to throw away free seams.
        ///
        /// All three must hold, and the third protects a new player who is still
        /// learning the parry on Monday: they have no earlier skilled week.
        /// Auto-strikes are never counted, here or anywhere in the skill sums.
        /// </remarks>
        public static bool IsSandbagging(
            double registrationSkillSum, int registrationPlayed,
            double laterSkillSum, int laterPlayed,
            bool provenInAnEarlierWeek)
        {
            if (!provenInAnEarlierWeek) return false;
            if (registrationPlayed <= 0 || laterPlayed < SandbagLaterMinStrikes) return false;
            double early = registrationSkillSum / registrationPlayed;
            double later = laterSkillSum / laterPlayed;
            return early <= SandbagRegistrationMax && later >= SandbagLaterMin;
        }

        /// <summary>Whether this week, on its own, shows the skill - written down for the weeks after.</summary>
        public static bool ShowsSkill(double skillSum, int played)
            => played >= SandbagLaterMinStrikes && skillSum / played >= ProvenSkillMin;
    }
}
