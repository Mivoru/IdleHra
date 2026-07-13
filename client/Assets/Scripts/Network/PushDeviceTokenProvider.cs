using UnityEngine;

namespace FolkIdle.Client.Network
{
    public class PushDeviceTokenProvider : MonoBehaviour
    {
        public byte PlatformFamily = 1;
        private readonly byte[] _tokenBytes = new byte[64];
        private int _tokenLength;

        public void SetTokenFromUtf8Bytes(byte[] sourceBytes, int sourceLength, byte platformFamily)
        {
            PlatformFamily = platformFamily;
            _tokenLength = sourceLength < 64 ? sourceLength : 64;
            for (int i = 0; i < 64; i++)
            {
                _tokenBytes[i] = i < _tokenLength ? sourceBytes[i] : (byte)0;
            }
        }

        public bool TryCopyToken(byte[] destination, out byte platformFamily)
        {
            platformFamily = PlatformFamily;
            if (_tokenLength <= 0 || destination.Length < 64)
            {
                return false;
            }

            for (int i = 0; i < 64; i++)
            {
                destination[i] = _tokenBytes[i];
            }

            return platformFamily > 0 && platformFamily <= 2;
        }
    }
}
