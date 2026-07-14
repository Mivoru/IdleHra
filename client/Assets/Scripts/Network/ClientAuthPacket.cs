using System;
using System.Runtime.InteropServices;

namespace FolkIdle.Client.Network
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClientAuthPacket
    {
        public Guid PlayerGuid;
        public Guid AuthenticatorToken;
        public long AssetHash;
        public long PlatformSignature;

        // Modul 29/45: unix-epoch-seconds timestamp of when this auth payload
        // was minted. The server rejects the handshake if this is older than
        // 24 hours. Must be set to the current time before sending - a zero or
        // stale value is rejected.
        public long EpochExpirationTime;
    }
}
