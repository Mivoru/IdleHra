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
    }
}
