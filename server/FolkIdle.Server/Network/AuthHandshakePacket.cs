using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    // Modul: mandatory first message on every WebSocket connection, replacing
    // the old ClientAuthPacket (a raw, unsigned Guid bearer token that any
    // client could mint for itself, auto-provisioning a brand new account on
    // first use with zero credential verification - see the removed
    // NetworkBroadcastSystem.AutoProvisionPlayerAsync). JwtToken holds a
    // UTF8-encoded compact JWT (header.payload.signature, base64url
    // segments), padded with trailing zero bytes; JwtTokenLength is the
    // exact byte count to read back out, since a JWT's real length varies
    // but this wire protocol only ever carries fixed-size structs (see
    // ClientCommandPacket's existing DeviceTokenBytes[64]/
    // RawTransactionReceipt[64] fields for the same fixed-buffer-plus-
    // length-field precedent this mirrors).
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct AuthHandshakePacket
    {
        public const int JwtTokenCapacity = 512;

        public fixed byte JwtToken[JwtTokenCapacity];
        public ushort JwtTokenLength;

        // Carried over unchanged from the old ClientAuthPacket - opt-in
        // client-tamper detection (see ClientCommandValidator.
        // ValidateAssetIntegrity), orthogonal to JWT identity verification.
        public long AssetHash;
        public long PlatformSignature;
    }
}
