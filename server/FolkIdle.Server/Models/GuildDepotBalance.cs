using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace FolkIdle.Server.Models
{
    public class GuildDepotBalance
    {
        public long GuildId { get; set; }
        public int ItemDefinitionId { get; set; }
        public int Quantity { get; set; }
    }
}
