namespace FolkIdle.Client.Network
{
    // Modul: Production Release Hardening, Part 1. Mirrors server
    // ProductIdHasher exactly (FNV-1a, 32-bit) - see that class's own doc
    // comment for why this replaces the previous string.GetHashCode()
    // (randomized per process by .NET, so it could never match a hash
    // computed on the server process). Both sides must hash the identical
    // bytes for ClientCommandPacket.TargetProductIdHash to ever resolve
    // against ContentRegistry.TryResolveProductIdFromHash server-side.
    public static class ProductIdHasher
    {
        private const uint FnvOffsetBasis = 2166136261U;
        private const uint FnvPrime = 16777619U;

        public static uint HashProductId(string productId)
        {
            uint hash = FnvOffsetBasis;
            for (int i = 0; i < productId.Length; i++)
            {
                hash ^= (byte)productId[i];
                hash *= FnvPrime;
            }
            return hash;
        }
    }
}
