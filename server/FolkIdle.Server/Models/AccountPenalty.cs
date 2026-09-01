using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: WHY an account is restricted, 2026-09-01.
    //
    // `PlayerRecords.IsQuarantined` and `Quarantine_Active` say THAT an account
    // is restricted and nothing more. Four detectors and, since 2026-08-10, the
    // admin ban tool all write those same two booleans, so after the fact it is
    // impossible to tell an anti-cheat flag from a moderator action, or to say
    // when either happened.
    //
    // The reason code went to TelemetryStreamer, which does not write to the
    // database, and to a Console.WriteLine that dies with the container - as it
    // did during a routine redeploy on the day this was written, taking the
    // only record of a live quarantine with it. The account that reported it
    // had been restricted since before that logging even existed, so the honest
    // answer to "why am I banned" was that nobody could know.
    //
    // A penalty is a row now. It is append-only: lifting one stamps LiftedAt
    // rather than deleting, because "this player was flagged in August and
    // cleared in September" is the history that makes a second flag readable.
    [Table("account_penalties")]
    public class AccountPenalty
    {
        [Key]
        public long Id { get; set; }

        public long PlayerId { get; set; }

        /// <summary>0 anti-cheat detector, 1 moderator action. See PenaltySource.</summary>
        public int Source { get; set; }

        /// <summary>
        /// The detector's own reason and detail codes, as passed to
        /// RequestShadowBan. Meaningless for a moderator action, which carries
        /// its reason in Note instead.
        /// </summary>
        public int ReasonCode { get; set; }

        public int DetailCode { get; set; }

        public long AppliedAtEpochMs { get; set; }

        /// <summary>Moderator username for an admin action; null for a detector.</summary>
        [MaxLength(64)]
        public string? AppliedBy { get; set; }

        /// <summary>Free text - why a moderator acted, or what a detector observed.</summary>
        [MaxLength(256)]
        public string? Note { get; set; }

        /// <summary>Null while the penalty stands. Stamped rather than deleted.</summary>
        public long? LiftedAtEpochMs { get; set; }

        [MaxLength(64)]
        public string? LiftedBy { get; set; }
    }

    public static class PenaltySource
    {
        /// <summary>One of the anti-cheat detectors. Automatic, and appealable.</summary>
        public const int AntiCheat = 0;

        /// <summary>A moderator using the admin tools. Deliberate, and attributable.</summary>
        public const int Admin = 1;

        public static string Describe(int source) => source switch
        {
            AntiCheat => "anti-cheat",
            Admin => "moderator",
            _ => "unknown"
        };
    }
}
