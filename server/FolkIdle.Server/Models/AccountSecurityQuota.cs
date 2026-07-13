using System;
using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class AccountSecurityQuota
    {
        [Key]
        public Guid AccountId { get; set; }
        public int TotalFloodInfractionsCount { get; set; }
        public long LastInfractionEpoch { get; set; }
        public bool IsPermanentlyBlacklisted { get; set; }
    }
}
