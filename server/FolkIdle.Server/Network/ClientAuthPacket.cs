using System;
using System.Runtime.InteropServices;

namespace FolkIdle.Server.Network
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ClientAuthPacket
    {
        public Guid PlayerGuid;
        public Guid AuthenticatorToken;
        public long AssetHash;
        public long PlatformSignature;

        // Modul 29/45: unix-epoch-seconds timestamp of when this auth payload
        // was minted client-side. The server rejects the handshake if this is
        // older than 24 hours (see NetworkBroadcastSystem.HandleClientLoopAsync).
        // A freshness/replay-window check, not a JWT signature.
        public long EpochExpirationTime;
    }
}
