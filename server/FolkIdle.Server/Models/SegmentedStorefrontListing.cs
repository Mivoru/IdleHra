using System.ComponentModel.DataAnnotations;

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
