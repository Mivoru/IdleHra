using System.ComponentModel.DataAnnotations;

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
