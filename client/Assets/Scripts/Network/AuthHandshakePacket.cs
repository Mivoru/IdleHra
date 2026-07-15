using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    // Mandatory first message on every WebSocket connection, replacing the
    // old ClientAuthPacket (a raw, unsigned Guid bearer token). Must mirror
    // the server's FolkIdle.Server.Network.AuthHandshakePacket byte-for-byte -
    // see NetworkPacketLayoutGuard on both sides. JwtToken holds a
    // UTF8-encoded compact JWT (header.payload.signature, base64url
    // segments), padded with trailing zero bytes; JwtTokenLength is the
    // exact byte count to read back out, mirroring ClientCommandPacket's
    // existing DeviceTokenBytes[64]/RawTransactionReceipt[64] fixed-buffer-
    // plus-length-field precedent.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct AuthHandshakePacket
    {
        public const int JwtTokenCapacity = 512;

        public fixed byte JwtToken[JwtTokenCapacity];
        public ushort JwtTokenLength;

        // Opt-in client-tamper detection (see ClientCommandValidator.
        // ValidateAssetIntegrity server-side), orthogonal to JWT identity
        // verification.
        public long AssetHash;
        public long PlatformSignature;
    }
}
