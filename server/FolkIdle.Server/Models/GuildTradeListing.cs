using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
