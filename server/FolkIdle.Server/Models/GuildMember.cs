using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    // Guild membership and raid-contribution ledger. PlayerRecord.GuildId is
    // the tick-loop-facing copy of membership (loaded into TickStatePayload
    // at login); this table is the authoritative membership row that
    // GuildManagementEngine creates/deletes, and both are always written
    // inside the same transaction so they cannot diverge.
    public class GuildMember
    {
        [Key]
        public long PlayerId { get; set; }

        public long GuildId { get; set; }
        public long ContributionPoints { get; set; }

        // 0 = Member, 1 = Warden, 2 = Elder, 3 = Chieftain (2026-11-03; see
        // GuildManagementEngine.RoleChieftain and the GuildFourRanks migration).
        // Administration is gated on the rank's NUMBER (higher = more power);
        // a guild always has exactly one Chieftain, handed on by
        // GuildManagementEngine.LeaveAsync, TransferLeadershipAsync, or
        // HandOnAbandonedGuildsAsync after 14 days away.
        public int Role { get; set; }

        public long WeeklyContributionPoints { get; set; }
    }
}
