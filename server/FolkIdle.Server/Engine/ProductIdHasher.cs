namespace FolkIdle.Server.Engine
{
    // Modul: Production Release Hardening, Part 1. Root cause of the
    // TargetProductIdHash mismatch this class exists to fix: the client's
    // former hash computation used string.GetHashCode(), which .NET
    // deliberately randomizes per process (a security hardening measure
    // against hash-flooding attacks) - the exact same product id string
    // ("gems_pack_small") produces a DIFFERENT hash on every server
    // process restart and every client run, so no fixed reverse-lookup
    // table could ever stay valid. FNV-1a is a simple, well-known,
    // deterministic non-cryptographic hash - the same string always
    // produces the same hash across processes, machines, and platforms,
    // which is the one property this use case actually needs (a stable
    // wire-format identifier for a small, fixed content catalog, not
    // resistance to deliberate collision attacks). Mirrored identically on
    // the client (see WebSocketClient's product-id hashing) - both sides
    // must compute over the exact same bytes for a hash emitted by the
    // client to ever resolve against ContentRegistry's server-side reverse
    // lookup table (see ContentRegistry.TryResolveProductIdFromHash).
    //
    // Iterates the string's UTF-16 chars directly (product ids are
    // ASCII-only content-authored slugs, so truncating each char to its
    // low byte is lossless for this catalog) rather than calling
    // Encoding.UTF8.GetBytes, which would allocate a byte[] - this keeps
    // hashing allocation-free, matching the zero-allocation requirement for
    // the billing hot path.
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
