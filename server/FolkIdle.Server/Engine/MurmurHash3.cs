namespace FolkIdle.Server.Engine
{
    public static class MurmurHash3
    {
        public static uint Hash64(long value, uint seed)
        {
            uint hash = seed;

            uint low = (uint)value;
            uint high = (uint)((ulong)value >> 32);

            hash = MixBody(hash, low);
            hash = MixBody(hash, high);

            hash ^= 8U;
            hash ^= hash >> 16;
            hash *= 0x85ebca6bU;
            hash ^= hash >> 13;
            hash *= 0xc2b2ae35U;
            hash ^= hash >> 16;

            return hash;
        }

        private static uint MixBody(uint hash, uint block)
        {
            uint k = block;
            k *= 0xcc9e2d51U;
            k = RotateLeft(k, 15);
            k *= 0x1b873593U;

            hash ^= k;
            hash = RotateLeft(hash, 13);
            hash = (hash * 5U) + 0xe6546b64U;
            return hash;
        }

        private static uint RotateLeft(uint value, int count)
        {
            return (value << count) | (value >> (32 - count));
        }
    }
}
