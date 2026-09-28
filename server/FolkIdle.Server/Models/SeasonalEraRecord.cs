using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class SeasonalEraRecord
    {
        [Key]
        public int EraId { get; set; }
        public long EndTimestamp { get; set; }
        public bool IsActive { get; set; }

        // Modul: THE OWNER DECIDES WHEN A SEASON ENDS (2026-09-28). A paused
        // era never rolls over on its own, whatever EndTimestamp says; the
        // admin panel pauses, resumes, moves the end and ends it on demand.
        // A 90-day wipe is a race, and with one player there was nobody to
        // race - it would only have taken 90 days of work away. A new era
        // inherits this flag from the era it replaces.
        public bool IsRolloverPaused { get; set; }
    }
}
