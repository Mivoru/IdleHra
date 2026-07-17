using System;
using System.ComponentModel.DataAnnotations;
using FolkIdle.Server.Domain.Combat;
using FolkIdle.Server.Domain.Economy;
using FolkIdle.Server.Domain.Social;
using FolkIdle.Server.Domain.Progression;
using FolkIdle.Server.Domain.Shared;

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
