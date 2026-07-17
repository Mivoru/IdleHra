using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

namespace FolkIdle.Server.Models
{
    public class SegmentedStorefrontListing
    {
        [Key]
        public int ListingId { get; set; }
        public int TargetCohort { get; set; }
        [Required]
        [MaxLength(255)]
        public string ProductIdentifier { get; set; } = string.Empty;
        public int DiamondPackageYield { get; set; }
        public int PriceInCents { get; set; }
    }
}
