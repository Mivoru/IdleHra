using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class PlayerSegmentationProfile
    {
        [Key]
        public long PlayerId { get; set; }
        public int CohortTag { get; set; }
        public int LifetimeValueCents { get; set; }
        public double ChurnRiskScore { get; set; }
    }
}
