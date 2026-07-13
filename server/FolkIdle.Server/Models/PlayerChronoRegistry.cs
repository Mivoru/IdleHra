namespace FolkIdle.Server.Models
{
    public class PlayerChronoRegistry
    {
        public long PlayerId { get; set; }
        public uint BankedChronoSeconds { get; set; }
        public long LastDisconnectTimestamp { get; set; }
    }
}
