using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    // Modul: Advanced Economy Refactoring, Part 3.3. Pending join request
    // for a guild whose JoinType is Application Required (1) - created by
    // GuildManagementEngine.JoinGuildAsync when the applicant clears the
    // level gates but the guild requires manual approval. One open
    // application per player per guild (enforced by the engine's
    // duplicate check inside its Serializable transaction, not a DB
    // constraint, matching how guild-name uniqueness is handled).
    public class GuildApplication
    {
        [Key]
        public long Id { get; set; }

        public long GuildId { get; set; }

        public long PlayerId { get; set; }

        // Applicant's CurrentLevel captured at application time - lets a
        // reviewing Leader see the level the gate actually admitted
        // without a join back to PlayerRecords.
        public int ApplicantLevel { get; set; }

        public long CreatedAtEpoch { get; set; }
    }
}
