using System;

namespace FolkIdle.Server.Models
{
    public class AccountChronoRegistry
    {
        public Guid AccountId { get; set; }
        public int BankedChronoSeconds { get; set; }
        public double ActiveSpeedMultiplier { get; set; } = 1.0;
        public long AccelerationTerminationEpoch { get; set; }
        public long LastClockSyncEpoch { get; set; }
    }
}
