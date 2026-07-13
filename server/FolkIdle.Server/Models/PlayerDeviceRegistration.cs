namespace FolkIdle.Server.Models
{
    public class PlayerDeviceRegistration
    {
        public long PlayerId { get; set; }
        public byte[] DeviceTokenRaw { get; set; } = new byte[64];
        public byte PlatformFamily { get; set; }
        public long TimestampRegistered { get; set; }
    }
}
