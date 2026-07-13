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
    }
}
