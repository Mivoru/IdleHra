using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class GuildTradeListing
    {
        [Key]
        public long Id { get; set; }
        public long GuildId { get; set; }
        public long? MarketEquipmentInstanceId { get; set; }
        public MarketEquipmentInstance? MarketEquipmentInstance { get; set; }
        public long? MarketOrderRecordId { get; set; }
        public MarketOrderRecord? MarketOrderRecord { get; set; }
    }
}
