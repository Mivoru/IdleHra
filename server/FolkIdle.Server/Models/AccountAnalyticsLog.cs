using System;
using System.ComponentModel.DataAnnotations;

namespace FolkIdle.Server.Models
{
    public class AccountAnalyticsLog
    {
        [Key]
        public long LogId { get; set; }
        public Guid AccountId { get; set; }
        public uint EventTypeHash { get; set; }
        public long TimestampEpoch { get; set; }
        public long PayloadMetric { get; set; }
    }
}
