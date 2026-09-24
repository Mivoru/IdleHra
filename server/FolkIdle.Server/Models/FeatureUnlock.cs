using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    // Modul: a feature that switches on ONCE, when the game has grown enough to
    // hold it, and never switches off again.
    //
    // Guild Wars is the first (GuildWarUnlock): the owner decided on 2026-09-23
    // that wars unlock at 50 qualifying players and 4 guilds of 3, and that the
    // unlock is ONE-WAY - a population that dips under the floor for a holiday
    // week must not switch off a running season. A live count cannot express
    // "once crossed", so the crossing is a row. Its absence means "not yet";
    // its presence means "for ever", whatever the count says today.
    //
    // A key rather than a column per feature, so the next population-gated
    // feature is a new constant and not a migration. The counts at the moment
    // of crossing are kept because "when, and on what numbers" is the first
    // question anyone asks about an unlock nobody remembers seeing.
    [Table("feature_unlocks")]
    public class FeatureUnlock
    {
        [Key]
        [MaxLength(64)]
        public string FeatureKey { get; set; } = string.Empty;

        public long UnlockedAtEpochMs { get; set; }

        public int QualifyingPlayersAtUnlock { get; set; }

        public int QualifyingGuildsAtUnlock { get; set; }
    }
}
