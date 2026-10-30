using FolkIdle.Server.Engine;

namespace FolkIdle.Server.Tests
{
    /// <summary>
    /// Puts War ranks on a test payload and runs it on the War tree, as
    /// hydration does for every player since rework slice 2.5.
    /// </summary>
    internal static class TestTree
    {
        internal static TickStatePayload With(TickStatePayload payload, params (int Id, byte Rank)[] ranks)
        {
            payload.SkillTreeVersion = WarTreeRegistry.CurrentVersion;
            foreach (var (id, rank) in ranks) payload.SkillRanks[id] = rank;
            return payload;
        }

        /// <summary>A node's value at a rank, in the node's own unit (15 tenths = 1.5).</summary>
        internal static float Value(int id, int rank) => WarTreeRegistry.Get(id)!.ValueAt(rank) / 10f;
    }
}
