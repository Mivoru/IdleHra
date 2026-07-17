using System;
using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
