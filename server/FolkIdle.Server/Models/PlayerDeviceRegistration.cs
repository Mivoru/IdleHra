namespace FolkIdle.Server.Models
{
    public class PlayerDeviceRegistration
    {
        public long PlayerId { get; set; }
        // Modul: empty rather than 64 zero bytes. The old default was the
        // width of the fixed wire field, and a row is only ever written with a
        // real token - a 64-byte default was a plausible-looking value that no
        // valid path ever produces.
        public byte[] DeviceTokenRaw { get; set; } = System.Array.Empty<byte>();
        public byte PlatformFamily { get; set; }
        public long TimestampRegistered { get; set; }
    }
}
